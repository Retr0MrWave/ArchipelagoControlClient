using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Ap.Control.Memory.Mac
{
    /// <summary>One image the game has loaded, as the shim reports it.</summary>
    public readonly record struct ShimImage(string Name, ulong Base, long Slide, string Uuid, bool IsExecutable);

    /// <summary>What the shim says about the process it is living in.</summary>
    /// <param name="Scratch">
    /// A buffer inside the game the client may write into and pass the address of. Several of the
    /// game's own methods take a pointer to a structure — the item grant takes a GlobalIDPointer,
    /// the ability grant a GlobalID — and this is the only memory the client can put one in. One
    /// fixed buffer is enough because requests are serialised and the pump runs one call at a time.
    /// </param>
    public readonly record struct ShimHello(int Pid, bool PumpTicking, ulong Beats,
        ulong Scratch, int ScratchLength, IReadOnlyList<ShimImage> Images)
    {
        /// <summary>The main executable — the Game binary, whose UUID keys the build profile.</summary>
        public ShimImage? Executable => Images.FirstOrDefault(i => i.IsExecutable) is { Name.Length: > 0 } image
            ? image
            : null;
    }

    /// <summary>The outcome of running something on the game's own thread.</summary>
    public readonly record struct ShimCall(bool Ok, ulong Result, ulong Beats, string? Error);

    /// <summary>A 32-bit value found in memory, with the bytes surrounding it.</summary>
    public readonly record struct ShimKeyHit(uint Value, long Address, byte[] Window);

    /// <summary>
    /// One of a class's vtables, found by walking the game's RTTI.
    /// </summary>
    /// <param name="Address">
    /// The address point — what an instance's first word holds, which is what a heap scan looks for.
    /// </param>
    /// <param name="OffsetToTop">
    /// Displacement to the start of the complete object: 0 on the primary vtable, negative on the
    /// secondaries a class with several bases also carries.
    /// </param>
    public readonly record struct ShimVtable(ulong Address, long OffsetToTop)
    {
        public bool IsPrimary => OffsetToTop == 0;
    }

    /// <summary>
    /// The result of an RTTI lookup. Carries the reason on failure rather than an empty list,
    /// because "this build renamed the class" and "the game is not running" want different answers
    /// from whoever asked.
    /// </summary>
    public readonly record struct ShimVtableLookup(ShimVtable[] Vtables, string? Error)
    {
        /// <summary>The vtable an instance's first word points at, if the class has one.</summary>
        public ShimVtable? Primary
        {
            get
            {
                foreach (ShimVtable vtable in Vtables)
                    if (vtable.IsPrimary) return vtable;
                return null;
            }
        }
    }

    /// <summary>
    /// Talks to <c>libapcontrol.dylib</c> inside the running game.
    ///
    /// The shim is the server, so this client's connection state IS the answer to "is the game
    /// running": there is no process to look up, no handle to open, and nothing to elevate. A
    /// failed connect simply means no game, and every operation reports that the same quiet way
    /// the Windows granters report a missing process.
    ///
    /// Requests are serialised. The shim runs one main-thread call at a time anyway, and the
    /// alternative — correlating concurrent replies by id — buys nothing at this traffic level.
    /// </summary>
    internal sealed class ShimClient : IDisposable
    {
        private const byte KindText = 0;
        private const byte KindJson = 1;
        private const byte KindBinary = 2;
        private const byte KindEvent = 3;

        /// <summary>How long to wait before trying the socket again after a failed connect.</summary>
        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(1);

        private readonly string _socketPath;
        private readonly object _gate = new();

        private Socket? _socket;
        private BlockingCollection<(byte Kind, byte[] Payload)>? _replies;
        private Thread? _reader;
        private DateTime _nextAttemptUtc = DateTime.MinValue;
        private long _nextId;
        private bool _disposed;

        /// <summary>Raised when the game asks to save. Off the caller's thread; handlers must be quick.</summary>
        internal event Action? SaveRequested;

        internal ShimClient(string? socketPath = null) => _socketPath = socketPath ?? DefaultSocketPath;

        /// <summary>
        /// Where the shim binds. Mirrors the C++ side, including the fallback it takes when a home
        /// directory would push the path past the 104-byte limit on a unix socket address.
        /// </summary>
        internal static string DefaultSocketPath
        {
            get
            {
                string? fromEnv = Environment.GetEnvironmentVariable("AP_SHIM_SOCKET");
                if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string path = Path.Combine(home, "Library", "Application Support", "Ap.Control", "shim.sock");
                return path.Length < 104 ? path : $"/tmp/ap-control-{Environment.UserName}.sock";
            }
        }

        internal string SocketPath => _socketPath;

        /// <summary>True if a connection is up right now. Does not attempt one.</summary>
        internal bool IsConnected
        {
            get { lock (_gate) return _socket is { Connected: true }; }
        }

        /// <summary>
        /// Ensure a connection, throttled so a client left running without the game does not spin
        /// on connect(2). Never throws: no shim is an expected state, not an error.
        /// </summary>
        internal bool EnsureConnected()
        {
            lock (_gate) return EnsureConnectedLocked();
        }

        private bool EnsureConnectedLocked()
        {
            if (_disposed) return false;
            if (_socket is { Connected: true }) return true;
            if (DateTime.UtcNow < _nextAttemptUtc) return false;

            _nextAttemptUtc = DateTime.UtcNow + RetryAfter;
            DropLocked();

            try
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(_socketPath));

                _socket = socket;
                _replies = new BlockingCollection<(byte, byte[])>();
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ap-shim-reader" };
                _reader.Start();
                return true;
            }
            catch (Exception)
            {
                DropLocked();
                return false;
            }
        }

        private void DropLocked()
        {
            try { _socket?.Dispose(); } catch { /* already gone */ }
            _socket = null;

            // Unblocks the reader thread's Take and any caller waiting on a reply.
            try { _replies?.CompleteAdding(); } catch { /* already completed */ }
            _replies = null;
            _reader = null;
        }

        /// <summary>
        /// Demultiplexes the socket: events are dispatched as they arrive, everything else is handed
        /// to whichever call is waiting. Only one request is ever outstanding, so no id matching is
        /// needed beyond the sanity check the caller does.
        /// </summary>
        private void ReadLoop()
        {
            Socket? socket;
            BlockingCollection<(byte, byte[])>? replies;
            lock (_gate) { socket = _socket; replies = _replies; }
            if (socket is null || replies is null) return;

            try
            {
                while (true)
                {
                    (byte kind, byte[] payload) = ReceiveFrame(socket);
                    if (kind == KindEvent) DispatchEvent(payload);
                    else replies.Add((kind, payload));
                }
            }
            catch (Exception)
            {
                // The game exited, or the socket broke. Either way the next request reconnects.
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_replies, replies)) DropLocked();
                    else { try { replies.CompleteAdding(); } catch { /* raced with a reconnect */ } }
                }
            }
        }

        private void DispatchEvent(byte[] payload)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("ev", out JsonElement name) &&
                    name.GetString() == "save_game")
                    SaveRequested?.Invoke();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[shim] unreadable event: {e.Message}");
            }
        }

        // --- operations ---------------------------------------------------------------------

        internal ShimHello? Hello()
        {
            if (Request("hello") is not { } response) return null;
            using (response)
            {
                JsonElement root = response.RootElement;
                var images = new List<ShimImage>();
                if (root.TryGetProperty("images", out JsonElement list))
                    foreach (JsonElement image in list.EnumerateArray())
                        images.Add(new ShimImage(
                            image.GetProperty("name").GetString() ?? "",
                            image.GetProperty("base").GetUInt64(),
                            image.GetProperty("slide").GetInt64(),
                            image.GetProperty("uuid").GetString() ?? "",
                            image.GetProperty("exe").GetBoolean()));

                // Older shims predate the scratch buffer. Reporting zero lets the granters say
                // "this shim cannot carry an item definition" rather than write to address 0.
                ulong scratch = root.TryGetProperty("scratch", out JsonElement at) ? at.GetUInt64() : 0;
                int scratchLength = root.TryGetProperty("scratch_len", out JsonElement len)
                    ? len.GetInt32()
                    : 0;

                return new ShimHello(root.GetProperty("pid").GetInt32(),
                    root.GetProperty("ticking").GetBoolean(),
                    root.GetProperty("beats").GetUInt64(), scratch, scratchLength, images);
            }
        }

        /// <summary>Whether the game is running frames, and how many it has run.</summary>
        internal (bool Ticking, ulong Beats)? Pump()
        {
            if (Request("pump") is not { } response) return null;
            using (response)
                return (response.RootElement.GetProperty("ticking").GetBoolean(),
                        response.RootElement.GetProperty("beats").GetUInt64());
        }

        /// <summary>
        /// Resolve a symbol by its Itanium mangled name without the Mach-O underscore — what
        /// <c>nm</c> prints minus one underscore. 0 when the game does not export it.
        /// </summary>
        internal ulong Resolve(string mangled)
        {
            if (Request($"sym {mangled}") is not { } response) return 0;
            using (response) return response.RootElement.GetProperty("addr").GetUInt64();
        }

        /// <summary>
        /// Find a class's vtables from its RTTI name, as the binary spells it —
        /// <c>"27GameInventoryComponentState"</c>, length-prefixed and without the <c>_ZTS</c>.
        ///
        /// This is what stands in for the Windows profile's vtable RVAs. Those have to be
        /// re-derived by hand for every game build; this is resolved from the running binary, so a
        /// game update does not touch it. The primary vtable comes first.
        /// </summary>
        /// <param name="image">Which loaded image to search; the main executable by default.</param>
        internal ShimVtableLookup Vtables(string rttiName, string? image = null)
        {
            if (Request($"vtable {rttiName}{(image is null ? "" : $" {image}")}") is not { } response)
                return new ShimVtableLookup([], "the shim is not reachable");

            using (response)
            {
                JsonElement root = response.RootElement;
                if (!root.GetProperty("ok").GetBoolean())
                    return new ShimVtableLookup([],
                        root.TryGetProperty("error", out JsonElement why)
                            ? why.GetString() ?? "the lookup failed"
                            : "the lookup failed");

                var found = new List<ShimVtable>();
                foreach (JsonElement vtable in root.GetProperty("vtables").EnumerateArray())
                    found.Add(new ShimVtable(vtable.GetProperty("addr").GetUInt64(),
                                             vtable.GetProperty("top").GetInt64()));
                return new ShimVtableLookup([.. found], null);
            }
        }

        /// <summary>
        /// Read up to <paramref name="length"/> bytes. A short result is normal — the request ran
        /// off the end of a mapping — and an empty one means nothing there was readable.
        /// </summary>
        internal byte[] Read(long address, int length)
        {
            lock (_gate)
            {
                if (!EnsureConnectedLocked()) return [];
                try
                {
                    SendTextLocked($"{NextId()} read {address} {length}");
                    using JsonDocument response = TakeJsonLocked();
                    if (!response.RootElement.GetProperty("ok").GetBoolean()) return [];

                    int got = response.RootElement.GetProperty("len").GetInt32();
                    if (got <= 0) return [];

                    (byte kind, byte[] body) = TakeFrameLocked();
                    return kind == KindBinary ? body : [];
                }
                catch (Exception e)
                {
                    Fault(e);
                    return [];
                }
            }
        }

        internal bool Write(long address, byte[] data)
        {
            lock (_gate)
            {
                if (!EnsureConnectedLocked()) return false;
                try
                {
                    SendTextLocked($"{NextId()} write {address} {data.Length}");
                    SendFrameLocked(KindBinary, data);
                    using JsonDocument response = TakeJsonLocked();
                    return response.RootElement.GetProperty("ok").GetBoolean();
                }
                catch (Exception e)
                {
                    Fault(e);
                    return false;
                }
            }
        }

        /// <summary>
        /// Every <paramref name="align"/>-aligned occurrence of a byte pattern in the regions a game
        /// object could live in. <paramref name="lookahead"/> is how far past a match the caller
        /// intends to read, so the shim can overlap its scan windows by that much.
        /// </summary>
        internal long[] Scan(byte[] pattern, int align = 1, int lookahead = 0, int limit = 0)
        {
            string hex = Convert.ToHexString(pattern);
            if (Request($"scan {hex} {align} {lookahead} {limit}") is not { } response) return [];

            using (response)
            {
                var hits = new List<long>();
                foreach (JsonElement hit in response.RootElement.GetProperty("hits").EnumerateArray())
                    hits.Add(hit.GetInt64());

                if (response.RootElement.TryGetProperty("truncated", out JsonElement truncated) &&
                    truncated.GetBoolean())
                    Console.Error.WriteLine("[shim] scan hit the result limit; some matches were dropped");

                return [.. hits];
            }
        }

        /// <summary>
        /// One sweep that finds every 4-aligned occurrence of any of <paramref name="values"/>, and
        /// brings back the bytes around each hit.
        ///
        /// Built for GameFlow reconciliation, which asks about two dozen variables at a time and
        /// has to tell a live map node from a snapshot. Doing that as a scan per variable, or as a
        /// read per hit, would be a full heap walk or hundreds of round trips every second.
        /// </summary>
        internal ShimKeyHit[] Keys(uint[] values, int pre, int post, int limit = 0)
        {
            if (values.Length == 0) return [];

            var line = new StringBuilder($"keys {pre} {post} {limit}");
            foreach (uint value in values) line.Append(' ').Append(value);

            lock (_gate)
            {
                if (!EnsureConnectedLocked()) return [];
                try
                {
                    SendTextLocked($"{NextId()} {line}");

                    int count;
                    using (JsonDocument response = TakeJsonLocked())
                    {
                        if (!response.RootElement.GetProperty("ok").GetBoolean()) return [];
                        count = response.RootElement.GetProperty("count").GetInt32();
                        if (count == 0) return [];
                    }

                    (byte kind, byte[] body) = TakeFrameLocked();
                    if (kind != KindBinary) return [];

                    // Fixed-size records: u32 value, u32 padding, u64 address, then the window.
                    int window = pre + post;
                    int stride = 16 + window;
                    if (body.Length < count * stride) return [];

                    var hits = new ShimKeyHit[count];
                    for (int i = 0; i < count; i++)
                    {
                        int at = i * stride;
                        hits[i] = new ShimKeyHit(
                            BitConverter.ToUInt32(body, at),
                            BitConverter.ToInt64(body, at + 8),
                            body[(at + 16)..(at + 16 + window)]);
                    }
                    return hits;
                }
                catch (Exception e)
                {
                    Fault(e);
                    return [];
                }
            }
        }

        /// <summary>
        /// Run <paramref name="function"/> on the game's own thread, at a frame boundary.
        ///
        /// <paramref name="integers"/> lands in x0-x7 and <paramref name="doubles"/> in d0-d7.
        /// AAPCS64 fills those from independent pools, so a callee taking
        /// <c>(void*, char, GID*, float)</c> reads three integers and one float regardless of their
        /// order in its signature.
        /// </summary>
        internal ShimCall Call(ulong function, ulong[] integers, ulong[] doubles, int timeoutMs = 5000)
        {
            var line = new StringBuilder($"call {function}");
            for (int i = 0; i < 8; i++) line.Append(' ').Append(i < integers.Length ? integers[i] : 0);
            for (int i = 0; i < 8; i++) line.Append(' ').Append(i < doubles.Length ? doubles[i] : 0);
            line.Append(' ').Append(timeoutMs);

            if (Request(line.ToString()) is not { } response)
                return new ShimCall(false, 0, 0, "the shim is not reachable — is the game running with it loaded?");

            using (response)
            {
                JsonElement root = response.RootElement;
                bool ok = root.GetProperty("ok").GetBoolean();
                return new ShimCall(
                    ok,
                    ok ? root.GetProperty("result").GetUInt64() : 0,
                    root.GetProperty("beats").GetUInt64(),
                    ok ? null : root.GetProperty("error").GetString());
            }
        }

        // --- plumbing -----------------------------------------------------------------------

        /// <summary>Send one request and take its JSON reply, or null if the shim is unreachable.</summary>
        private JsonDocument? Request(string opAndArgs)
        {
            lock (_gate)
            {
                if (!EnsureConnectedLocked()) return null;
                try
                {
                    SendTextLocked($"{NextId()} {opAndArgs}");
                    return TakeJsonLocked();
                }
                catch (Exception e)
                {
                    Fault(e);
                    return null;
                }
            }
        }

        private long NextId() => Interlocked.Increment(ref _nextId);

        private void Fault(Exception e)
        {
            Console.Error.WriteLine($"[shim] {e.Message}");
            DropLocked();
        }

        private void SendTextLocked(string line) => SendFrameLocked(KindText, Encoding.UTF8.GetBytes(line));

        private void SendFrameLocked(byte kind, byte[] payload)
        {
            Socket socket = _socket ?? throw new IOException("the shim connection is gone");

            var header = new byte[5];
            BitConverter.TryWriteBytes(header, payload.Length + 1);
            header[4] = kind;

            socket.Send(header, SocketFlags.None);
            if (payload.Length > 0) socket.Send(payload, SocketFlags.None);
        }

        private JsonDocument TakeJsonLocked()
        {
            (byte kind, byte[] payload) = TakeFrameLocked();
            if (kind != KindJson) throw new IOException($"expected a json reply, got frame kind {kind}");
            return JsonDocument.Parse(payload);
        }

        private (byte Kind, byte[] Payload) TakeFrameLocked()
        {
            BlockingCollection<(byte, byte[])> replies =
                _replies ?? throw new IOException("the shim connection is gone");

            // Generous: a main-thread call can legitimately sit for its own timeout, and the shim
            // answers even then. This only has to outlast that.
            if (!replies.TryTake(out (byte Kind, byte[] Payload) frame, TimeSpan.FromSeconds(70)))
                throw new IOException("the shim stopped answering");
            return frame;
        }

        private static (byte Kind, byte[] Payload) ReceiveFrame(Socket socket)
        {
            byte[] header = ReceiveExactly(socket, 5);
            int total = BitConverter.ToInt32(header, 0);
            if (total < 1) throw new IOException($"the shim sent a {total}-byte frame");

            byte[] payload = total > 1 ? ReceiveExactly(socket, total - 1) : [];
            return (header[4], payload);
        }

        private static byte[] ReceiveExactly(Socket socket, int count)
        {
            var buffer = new byte[count];
            int at = 0;
            while (at < count)
            {
                int got = socket.Receive(buffer, at, count - at, SocketFlags.None);
                if (got <= 0) throw new IOException("the shim closed the connection");
                at += got;
            }
            return buffer;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                DropLocked();
            }
        }
    }
}
