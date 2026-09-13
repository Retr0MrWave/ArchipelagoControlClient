namespace Ap.Control.SaveFile
{
    /// <summary>
    /// Where Control keeps its save on macOS.
    ///
    /// Written as a search rather than a constant because the exact location is not yet confirmed:
    /// the machine this was developed on had never saved the game, and the binary's own strings
    /// (<c>savegame-slot-%02d</c>, <c>persistent</c>, and platform.dylib's talk of savegame slot
    /// sizes) suggest a slot container rather than the bare <c>persistent.chunk</c> the Windows
    /// build writes. Searching the plausible roots for the newest save-shaped file costs a few
    /// milliseconds once at startup and is right under either scheme.
    ///
    /// A player whose save lives somewhere unexpected can always pass --save.
    /// </summary>
    internal static class MacSaveLocations
    {
        private static readonly string[] FilePatterns = ["persistent*", "savegame-slot-*"];

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

        /// <summary>The newest save-shaped file under any root, or null if there is not one yet.</summary>
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
                        newest = file;
                        newestWrite = written;
                    }
                }
            }
            return newest;
        }

        /// <summary>
        /// The path to watch: the real save if there is one, otherwise the place one is most likely
        /// to appear. The watcher tolerates the second case and picks the file up when it shows up,
        /// which is what a player starting a brand-new game will do.
        /// </summary>
        internal static string Preferred()
            => Discover()
               ?? Path.Combine(Roots().First(), "persistent.chunk");

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
