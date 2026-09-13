namespace Ap.Control.SaveFile
{
    /// <summary>
    /// Where Control keeps its save on macOS.
    ///
    /// Confirmed against a real playthrough: the macOS build writes a Steam Cloud slot container,
    /// <c>Steam/userdata/&lt;id&gt;/870780/remote/savegame-slot-NN_persistent</c>, and not the bare
    /// <c>persistent.chunk</c> the Windows build keeps in the bundle-identifier folder. The slot
    /// splits a save across four files — <c>_global</c>, <c>_hub</c>, <c>_meta</c> and
    /// <c>_persistent</c> — of which only <c>_persistent</c> carries the chunks this client reads.
    /// The other three are written within seconds of it, so picking by timestamp alone is a
    /// coin flip; the name and the header decide instead.
    ///
    /// Still written as a search rather than a constant: the Steam user id is per-account, the slot
    /// number varies, and the Epic build has not been checked. A player whose save lives somewhere
    /// unexpected can always pass --save.
    /// </summary>
    internal static class MacSaveLocations
    {
        /// <summary>
        /// Names that can hold the persistent chunk. Deliberately not a bare
        /// <c>savegame-slot-*</c>: that also matches <c>_meta</c>, which is a 32-byte stub with a
        /// different header, and <c>_hub</c> / <c>_global</c>, which are real saves of other scopes.
        /// </summary>
        private static readonly string[] FilePatterns = ["persistent*", "savegame-slot-*_persistent"];

        /// <summary>The chunk-file header <see cref="Models.Header"/> insists on.</summary>
        private static ReadOnlySpan<byte> Magic => [6, 0, 0, 0, 6, 0, 0, 0];

        /// <summary>Directories the save could be under, most likely first.</summary>
        internal static IEnumerable<string> Roots()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string support = Path.Combine(home, "Library", "Application Support");

            // The bundle identifier's own folder: the game already writes renderer.ini and its
            // lighting cache here, so it is where a save most plausibly joins them.
            yield return Path.Combine(support, "com.remedygames.control-ue-steam");
            yield return Path.Combine(support, "com.remedygames.control-ue-epic");

            // Steam Cloud keeps a synchronised copy per user under the app id.
            string userdata = Path.Combine(support, "Steam", "userdata");
            if (Directory.Exists(userdata))
                foreach (string user in SafeDirectories(userdata))
                    yield return Path.Combine(user, "870780", "remote");
        }

        /// <summary>The newest real persistent chunk under any root, or null if there is not one yet.</summary>
        internal static string? Discover()
        {
            string? newest = null;
            DateTime newestWrite = DateTime.MinValue;

            foreach (string root in Roots())
            {
                if (!Directory.Exists(root)) continue;

                foreach (string pattern in FilePatterns)
                {
                    foreach (string file in SafeFiles(root, pattern))
                    {
                        DateTime written = File.GetLastWriteTimeUtc(file);
                        if (written <= newestWrite) continue;
                        if (!HasChunkHeader(file)) continue;
                        newest = file;
                        newestWrite = written;
                    }
                }
            }
            return newest;
        }

        /// <summary>
        /// Whether a file opens with the chunk magic. The name narrows the field; this rejects the
        /// rest, so a slot file that is renamed, truncated or half-written never becomes the thing
        /// the watcher points at.
        /// </summary>
        private static bool HasChunkHeader(string path)
        {
            try
            {
                using FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                Span<byte> head = stackalloc byte[8];
                return fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                       && head.SequenceEqual(Magic);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// The path to watch: the real save if there is one, otherwise the place one is most likely
        /// to appear. The watcher tolerates the second case and picks the file up when it shows up,
        /// which is what a player starting a brand-new game will do.
        /// </summary>
        internal static string Preferred()
            => Discover()
               ?? ExpectedSlot()
               ?? Path.Combine(Roots().First(), "persistent.chunk");

        /// <summary>
        /// Where a first save will land for a player who has never made one: slot 00 of the Steam
        /// Cloud folder, if this machine has one. Guessing this rather than the bundle-id folder is
        /// what lets the watcher be waiting in the right directory before the game ever saves.
        /// </summary>
        private static string? ExpectedSlot()
        {
            foreach (string root in Roots())
            {
                if (root.EndsWith("remote", StringComparison.Ordinal) && Directory.Exists(root))
                    return Path.Combine(root, "savegame-slot-00_persistent");
            }
            return null;
        }

        private static string[] SafeDirectories(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }

        private static string[] SafeFiles(string root, string pattern)
        {
            try
            {
                // Recursive but forgiving: a folder we cannot read is skipped rather than fatal.
                return Directory.GetFiles(root, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true,
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}
