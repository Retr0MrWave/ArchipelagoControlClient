using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Ap.Control.Memory.Mac;

namespace Ap.Control.Setup
{
    /// <summary>
    /// Puts the helper dylib and the Steam launch wrapper where the game can load them.
    ///
    /// The client carries both as embedded resources and writes them itself rather than asking the
    /// player to copy files, for a reason beyond convenience: macOS attaches
    /// <c>com.apple.quarantine</c> to anything a browser downloads, and a quarantined dylib is
    /// refused inside a hardened-runtime process. A file this writes is not quarantined in the first
    /// place — the explicit clear afterwards is belt and braces for the case where the whole
    /// directory inherited the flag from an extracted archive.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class MacInstaller
    {
        internal const string DylibName = "libapcontrol.dylib";
        internal const string WrapperName = "apcontrol-launch.sh";

        /// <summary>
        /// Where both files go, and where the shim binds its socket. Empty if the home directory
        /// cannot be determined.
        /// </summary>
        /// <remarks>
        /// The emptiness matters. <c>Path.Combine("", "Library", …)</c> is a perfectly good
        /// *relative* path, so a client that cannot resolve a home directory would write a complete,
        /// correct-looking install into whatever directory it happened to be started from, report
        /// success, and leave the game loading nothing. Callers check.
        /// </remarks>
        internal static string Directory
        {
            get
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.IsPathRooted(home)
                    ? Path.Combine(home, "Library", "Application Support", "Ap.Control")
                    : "";
            }
        }

        /// <summary>The line to paste into Steam's launch options, quoted for the space in the path.</summary>
        internal static string LaunchOption => $"\"{Path.Combine(Directory, WrapperName)}\" %command%";

        /// <summary>What an install did, or why it could not.</summary>
        internal readonly record struct Result(
            bool Ok, string Directory, bool WroteDylib, bool GameRunning, string? Problem);

        internal static Result Install()
        {
            if (!OperatingSystem.IsMacOS())
                return new Result(false, "", false, false,
                    "the launch wrapper is a macOS arrangement; this platform drives the game directly.");

            string target = Directory;
            if (target.Length == 0)
                return new Result(false, "", false, false,
                    "your home directory could not be determined, so there is nowhere to install to. "
                    + "Is HOME set?");

            try
            {
                System.IO.Directory.CreateDirectory(target);

                // The wrapper is always embedded; the dylib only when the build had one to embed.
                // Saying which is missing beats writing half an install and letting the game fail to
                // find a symbol later.
                byte[]? dylib = Resource(DylibName);
                byte[]? wrapper = Resource(WrapperName);
                if (wrapper is null)
                    return new Result(false, target, false, false,
                        "this client was built without the launch wrapper.");

                if (dylib is not null) WriteAtomic(Path.Combine(target, DylibName), dylib, executable: false);
                WriteAtomic(Path.Combine(target, WrapperName), wrapper, executable: true);

                Unquarantine(target);

                return new Result(true, target, dylib is not null, ShimIsLoaded(),
                    dylib is not null
                        ? null
                        : "this client was built without the helper dylib, so only the wrapper was "
                        + "installed. Build it from a source checkout with "
                        + "`make -C native/apshim install`.");
            }
            catch (Exception e)
            {
                return new Result(false, target, false, false, e.Message);
            }
        }

        /// <summary>
        /// Write via a temporary file and a rename, never over the bytes in place.
        /// </summary>
        /// <remarks>
        /// A running game has the dylib mapped, and overwriting it means the next page the game
        /// faults in comes from a different build — a crash in the player's session at a moment
        /// unrelated to the install. A rename leaves that process on the old inode until it exits.
        /// The same reasoning is in <c>native/apshim/Makefile</c>; the consequence for the player is
        /// that a reinstall never takes effect until Control is restarted, which
        /// <see cref="Result.GameRunning"/> exists to warn about.
        /// </remarks>
        private static void WriteAtomic(string path, byte[] content, bool executable)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.new");
            File.WriteAllBytes(temporary, content);
            if (executable) File.SetUnixFileMode(temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(temporary, path, overwrite: true);
        }

        private static byte[]? Resource(string name)
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        /// <summary>Best effort: the files were not quarantined, but the directory may have been.</summary>
        private static void Unquarantine(string path)
        {
            try
            {
                using Process? xattr = Process.Start(new ProcessStartInfo("/usr/bin/xattr",
                    ["-dr", "com.apple.quarantine", path])
                { RedirectStandardError = true });
                xattr?.WaitForExit(5000);
            }
            catch { /* absent or refused; the files this wrote were never quarantined anyway */ }
        }

        /// <summary>
        /// Whether a shim is already loaded in a running game — i.e. whether this install takes
        /// effect now or only after a restart.
        /// </summary>
        /// <remarks>
        /// Asked by connecting to the socket rather than by looking for a process called "Game",
        /// because the socket answers the question that actually matters. A game running without the
        /// shim has nothing stale to keep and needs no warning; a game running with one does, and
        /// will go on using it however many times the file underneath is replaced. A socket left
        /// behind by a crash fails to connect, so it does not produce a false warning either.
        /// </remarks>
        private static bool ShimIsLoaded()
        {
            try
            {
                using var shim = new ShimClient();
                return shim.EnsureConnected();
            }
            catch { return false; }
        }

        /// <summary>Whether Control is up at all, shim or no shim. For <see cref="Launch"/>.</summary>
        internal static bool IsGameRunning()
        {
            try { return Process.GetProcessesByName("Game").Length > 0; }
            catch { return false; }
        }

        /// <summary>
        /// Start Control directly, with the shim loaded, instead of going through Steam.
        /// </summary>
        /// <remarks>
        /// The development path. Steam remains the supported one for players because it keeps the
        /// overlay, cloud saves and playtime working — but it also means editing launch options in
        /// a GUI, which is a poor fit for a debug loop.
        ///
        /// The two Steam variables matter: without them <c>SteamAPI_RestartAppIfNecessary</c> bounces
        /// the game through the Steam client, which relaunches it through LaunchServices and drops
        /// the environment this just set — so the shim would not load and nothing would say why.
        /// </remarks>
        internal static (bool Ok, string? Problem) Launch(string? gamePath = null)
        {
            if (!OperatingSystem.IsMacOS()) return (false, "--launch is macOS only.");

            string? binary = gamePath is null ? FindGameBinary() : ResolveBundle(gamePath);
            if (binary is null)
                return (false, "Control was not found in a Steam library — pass --game <Game.app>.");
            if (!File.Exists(binary))
                return (false, $"no game binary at {binary}.");

            if (Directory.Length == 0)
                return (false, "your home directory could not be determined.");

            string dylib = Path.Combine(Directory, DylibName);
            if (!File.Exists(dylib))
                return (false, $"{dylib} is not installed — run `Ap.Control install-launcher` first.");

            var start = new ProcessStartInfo(binary) { UseShellExecute = false };
            string existing = Environment.GetEnvironmentVariable("DYLD_INSERT_LIBRARIES") ?? "";
            start.Environment["DYLD_INSERT_LIBRARIES"] = existing.Length > 0 ? $"{existing}:{dylib}" : dylib;
            start.Environment["SteamAppId"] = "870780";
            start.Environment["SteamGameId"] = "870780";

            try
            {
                return Process.Start(start) is null
                    ? (false, "the game did not start.")
                    : (true, null);
            }
            catch (Exception e) { return (false, e.Message); }
        }

        /// <summary>
        /// The executable inside a bundle, for a <c>--game</c> that names the <c>.app</c> — which is
        /// what a player has to hand, and what the patcher already accepts. Anything else is taken
        /// as the binary itself.
        /// </summary>
        private static string ResolveBundle(string path)
        {
            string trimmed = path.TrimEnd('/');
            return trimmed.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(trimmed, "Contents", "MacOS", "Game")
                : trimmed;
        }

        /// <summary>The Mach-O inside Control.app, from the usual Steam library locations.</summary>
        internal static string? FindGameBinary()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string steam = Path.Combine(home, "Library", "Application Support", "Steam");

            var roots = new List<string> { Path.Combine(steam, "steamapps", "common") };
            roots.AddRange(ExtraLibraries(Path.Combine(steam, "steamapps", "libraryfolders.vdf")));

            foreach (string root in roots)
            {
                string candidate = Path.Combine(root, "Control", "Game.app", "Contents", "MacOS", "Game");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Extra Steam libraries, from the "path" entries of libraryfolders.vdf.
        /// </summary>
        /// <remarks>
        /// Read with a substring search rather than a VDF parser: the only thing wanted here is a
        /// list of directories to probe, every candidate is checked with <c>File.Exists</c> anyway,
        /// and a malformed line costs a miss rather than a wrong answer.
        /// </remarks>
        private static IEnumerable<string> ExtraLibraries(string vdf)
        {
            if (!File.Exists(vdf)) yield break;

            string[] lines;
            try { lines = File.ReadAllLines(vdf); } catch { yield break; }

            foreach (string line in lines)
            {
                int key = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                if (key < 0) continue;

                int open = line.IndexOf('"', key + 6);
                if (open < 0) continue;
                int close = line.IndexOf('"', open + 1);
                if (close < 0) continue;

                string path = line[(open + 1)..close].Replace("\\\\", "/");
                if (path.Length > 0) yield return Path.Combine(path, "steamapps", "common");
            }
        }
    }
}
