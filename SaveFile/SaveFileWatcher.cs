using Ap.Control.Models;
using Ap.Control.Utils.Interfaces;
using Ap.Control.Utils.Save;

namespace Ap.Control.SaveFile
{
    public sealed class SaveFileWatcher : ISaveWatcher
    {
        private readonly string _fullPath;
        private readonly string _directory;
        private readonly string _fileName;
        private readonly ISaveFileParser _parser;
        private readonly int _debounceMs;
        private readonly int _readRetries;
        private readonly int _readRetryDelayMs;

        private readonly List<ISaveChangeNotifier> _notifiers = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _cts = new();

        private FileSystemWatcher? _fsw;
        private Timer? _debounce;
        private Timer? _awaitDirectory;
        private ControlSave? _current;
        private ulong _lastHash;
        private bool _started;
        private bool _disposed;

        public event EventHandler<SaveChangedEventArgs>? SaveChanged;

        public event EventHandler<SaveWatchErrorEventArgs>? Error;

        public ControlSave? Current => _current;

        public SaveFileWatcher(
            string savePath,
            ISaveFileParser? parser = null,
            int debounceMs = 400,
            int readRetries = 10,
            int readRetryDelayMs = 100)
        {
            _fullPath = Path.GetFullPath(savePath);
            _directory = Path.GetDirectoryName(_fullPath)
                ?? throw new ArgumentException("Save path has no directory.", nameof(savePath));
            _fileName = Path.GetFileName(_fullPath);
            _parser = parser ?? new ControlSaveParser();
            _debounceMs = debounceMs;
            _readRetries = readRetries;
            _readRetryDelayMs = readRetryDelayMs;
        }

        public ISaveWatcher AddNotifier(ISaveChangeNotifier notifier)
        {
            ArgumentNullException.ThrowIfNull(notifier);
            _notifiers.Add(notifier);
            return this;
        }

        public async Task StartAsync(bool emitInitial = false, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

            if (File.Exists(_fullPath))
            {
                var (bytes, save) = await TryReadAndParseAsync(linked.Token).ConfigureAwait(false);
                if (bytes is not null && save is not null)
                {
                    _lastHash = Fnv1a(bytes);
                    if (emitInitial)
                    {
                        var diff = SaveDiffer.Diff(null, save);
                        _current = save;
                        if (diff.HasChanges)
                            await DispatchAsync(save, previous: null, diff, linked.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        _current = save;
                    }
                }
            }

            _debounce = new Timer(_ => _ = ProcessChangeAsync(), null, Timeout.Infinite, Timeout.Infinite);
            Attach();
        }

        /// <summary>
        /// Start watching, or arrange to start once the directory exists.
        ///
        /// A save directory that is not there yet is an ordinary state, not an error: a player who
        /// has never saved this game does not have one, and on macOS the client defaults to the
        /// file source, so this is the first run of every new installation. FileSystemWatcher throws
        /// on a missing directory, which would take the whole client down over it.
        /// </summary>
        private void Attach()
        {
            if (_disposed || _fsw is not null) return;

            if (!Directory.Exists(_directory))
            {
                _awaitDirectory ??= new Timer(_ => Attach(), null, DirectoryPollMs, DirectoryPollMs);
                return;
            }

            var watcher = new FileSystemWatcher(_directory, _fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                             | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            watcher.Changed += OnFsEvent;
            watcher.Created += OnFsEvent;
            watcher.Renamed += OnFsEvent;
            watcher.EnableRaisingEvents = true;
            _fsw = watcher;

            Timer? waiting = Interlocked.Exchange(ref _awaitDirectory, null);
            waiting?.Dispose();

            // The save may have appeared while we were waiting for its folder.
            _debounce?.Change(_debounceMs, Timeout.Infinite);
        }

        /// <summary>How often to look for a save directory that does not exist yet.</summary>
        private const int DirectoryPollMs = 5000;

        private void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            _debounce?.Change(_debounceMs, Timeout.Infinite);
        }

        /// <summary>
        /// Look at the save again shortly, as if the filesystem had reported a change.
        ///
        /// For callers that know a save is coming before the filesystem does — on macOS the shim
        /// sees the game's own saveGame call. Harmless if nothing changed: the content hash decides
        /// whether anything is dispatched.
        /// </summary>
        public void RequestRescan() => _debounce?.Change(_debounceMs, Timeout.Infinite);

        private async Task ProcessChangeAsync()
        {
            if (_cts.IsCancellationRequested) return;
            await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                var (bytes, save) = await TryReadAndParseAsync(_cts.Token).ConfigureAwait(false);
                if (bytes is null || save is null) return;

                ulong hash = Fnv1a(bytes);
                if (hash == _lastHash) return;
                _lastHash = hash;

                var previous = _current;
                var diff = SaveDiffer.Diff(previous, save);
                _current = save;

                if (diff.HasChanges)
                    await DispatchAsync(save, previous, diff, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task DispatchAsync(ControlSave save, ControlSave? previous, SaveDiff diff, CancellationToken ct)
        {
            var args = new SaveChangedEventArgs
            {
                Save = save,
                Previous = previous,
                Diff = diff,
                Path = _fullPath,
            };

            SaveChanged?.Invoke(this, args);

            foreach (var notifier in _notifiers)
            {
                try
                {
                    await notifier.NotifyAsync(args, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    OnError(ex);
                }
            }
        }

        private async Task<(byte[]? Bytes, ControlSave? Save)> TryReadAndParseAsync(CancellationToken ct)
        {
            for (int attempt = 0; attempt < _readRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    byte[] bytes;
                    using (var fs = new FileStream(_fullPath, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        bytes = new byte[fs.Length];
                        int read = 0;
                        while (read < bytes.Length)
                        {
                            int n = await fs.ReadAsync(bytes.AsMemory(read), ct).ConfigureAwait(false);
                            if (n == 0) break;
                            read += n;
                        }
                        if (read != bytes.Length) throw new EndOfStreamException("Short read.");
                    }

                    var save = _parser.Parse(bytes);
                    return (bytes, save);
                }
                catch (Exception) when (attempt < _readRetries - 1)
                {
                    await Task.Delay(_readRetryDelayMs, ct).ConfigureAwait(false);
                }
            }
            return (null, null);
        }

        private void OnError(Exception ex)
            => Error?.Invoke(this, new SaveWatchErrorEventArgs { Exception = ex, Path = _fullPath });

        private static ulong Fnv1a(ReadOnlySpan<byte> data)
        {
            const ulong offset = 14695981039346656037;
            const ulong prime = 1099511628211;
            ulong hash = offset;
            foreach (byte b in data)
            {
                hash ^= b;
                hash *= prime;
            }
            return hash;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts.Cancel(); } catch { /* ignore */ }
            if (_fsw is not null)
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Changed -= OnFsEvent;
                _fsw.Created -= OnFsEvent;
                _fsw.Renamed -= OnFsEvent;
                _fsw.Dispose();
            }
            Interlocked.Exchange(ref _awaitDirectory, null)?.Dispose();
            _debounce?.Dispose();
            _gate.Dispose();
            _cts.Dispose();
        }
    }
}
