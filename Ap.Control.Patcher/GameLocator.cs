using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Ap.Control.Patcher
{
    /// <summary>
    /// Finds Control's install directory. The original Python patchers hardcoded one absolute path,
    /// which works on exactly one machine; anything distributed to players has to look for itself.
    ///
    /// Order: an explicit --game path, then Steam (the registry on Windows, the one fixed Application
    /// Support location on macOS, then every library in libraryfolders.vdf), then the usual fixed
    /// locations for Steam / Epic / GOG installs.
    /// </summary>
    internal static class GameLocator
    {
        private const string GameFolder = "Control";

        /// <summary>A directory is the game if it holds the package files we patch.</summary>
        internal static bool IsGameDir(string dir) => Directory.Exists(Path.Combine(dir, "data_packfiles"));

        /// <summary>
        /// The patchable directory at or just inside <paramref name="dir"/>, or null if there isn't one.
        ///
        /// On Windows the install folder IS the patchable folder. On macOS the game ships as an app
        /// bundle and the packages live at <c>Game.app/Contents/Resources/data_packfiles</c>, so the
        /// folder a player would naturally point at — the Steam library's <c>Control</c> — is two
        /// levels above the one being patched. Accept every level rather than make them guess.
        /// </summary>
        internal static string? NormalizeGameDir(string dir)
        {
            if (IsGameDir(dir)) return dir;

            // Given …/common/Control, or given …/common/Control/Game.app itself.
            foreach (string relative in new[]
            {
                Path.Combine("Game.app", "Contents", "Resources"),
                Path.Combine("Contents", "Resources"),
            })
            {
                string candidate = Path.Combine(dir, relative);
                if (IsGameDir(candidate)) return candidate;
            }

            // A differently-named bundle — a renamed install, or the scratch copy that testing a
            // patch against something other than the live game calls for.
            foreach (string app in SafeDirectories(dir, "*.app"))
            {
                string candidate = Path.Combine(app, "Contents", "Resources");
                if (IsGameDir(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Resolve the install dir, or throw with guidance on what to pass instead.</summary>
        internal static string Resolve(string? explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string dir = Path.GetFullPath(explicitPath);
                return NormalizeGameDir(dir)
                    ?? throw new PatchException(
                        $"--game path has no data_packfiles folder: {dir}\n{PointAtHint}");
            }

            foreach (string candidate in Candidates())
                if (NormalizeGameDir(candidate) is { } dir)
                    return dir;

            throw new PatchException(
                "could not find Control automatically.\n" +
                "Pass the install folder explicitly, e.g.:\n" +
                $"  {ExampleCommand}");
        }

        private static string PointAtHint => OperatingSystem.IsMacOS()
            ? "Point it at the Control folder in your Steam library, at Game.app, or at\n" +
              "Game.app/Contents/Resources — any of the three works."
            : "Point it at the folder containing Control_DX12.exe.";

        private static string ExampleCommand => OperatingSystem.IsMacOS()
            ? "Ap.Control.Patcher status --game \"$HOME/Library/Application Support/Steam/steamapps/common/Control\""
            : "Ap.Control.Patcher status --game \"D:\\SteamLibrary\\steamapps\\common\\Control\"";

        private static IEnumerable<string> Candidates()
        {
            foreach (string lib in SteamLibraries())
                yield return Path.Combine(lib, "steamapps", "common", GameFolder);

            // Non-Steam and unusual-but-common layouts.
            foreach (string root in FixedRoots())
            {
                yield return Path.Combine(root, "Steam", "steamapps", "common", GameFolder);
                yield return Path.Combine(root, "SteamLibrary", "steamapps", "common", GameFolder);

                // Neither storefront ships a macOS build of Control, so only look on Windows.
                if (!OperatingSystem.IsMacOS())
                {
                    yield return Path.Combine(root, "Epic Games", GameFolder);
                    yield return Path.Combine(root, "GOG Galaxy", "Games", GameFolder);
                }
                yield return Path.Combine(root, GameFolder);
            }
        }

        private static IEnumerable<string> FixedRoots()
        {
            if (OperatingSystem.IsMacOS())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                yield return Path.Combine(home, "Library", "Application Support");
                yield return "/Applications";
                yield return Path.Combine(home, "Applications");

                // The macOS equivalent of "parked on a second drive": an external disk, which mounts
                // under /Volumes. DriveInfo would also report every system and synthetic volume.
                foreach (string volume in SafeDirectories("/Volumes", "*"))
                    yield return volume;
                yield break;
            }

            foreach (var v in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            })
                if (!string.IsNullOrEmpty(v)) yield return v;

            // Games are routinely parked on a second drive, so sweep fixed drive roots too.
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                    yield return d.RootDirectory.FullName;
        }

        /// <summary>Steam's own install plus every configured library folder.</summary>
        private static IEnumerable<string> SteamLibraries()
        {
            string? steam = SteamPath();
            if (steam is null) yield break;

            yield return steam;

            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;

            // The VDF is a small nested key/value text format. Rather than take a parser dependency for
            // one file, pull the "path" values out directly — they are the only thing needed here.
            foreach (string line in File.ReadLines(vdf))
            {
                string t = line.Trim();
                if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;

                int open = t.IndexOf('"', 6);
                int close = open < 0 ? -1 : t.IndexOf('"', open + 1);
                if (open >= 0 && close > open)
                    yield return t[(open + 1)..close].Replace(@"\\", @"\");
            }
        }

        private static string? SteamPath()
        {
            if (OperatingSystem.IsMacOS())
            {
                // Steam for Mac installs in exactly one place, and there is no registry to ask.
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Steam");
                return Directory.Exists(path) ? path : null;
            }
            return OperatingSystem.IsWindows() ? WindowsSteamPath() : null;
        }

        [SupportedOSPlatform("windows")]
        private static string? WindowsSteamPath()
        {
            // Registry is authoritative when present; a missing or unreadable key just means we fall
            // through to the fixed-path sweep, so failure here is not worth surfacing.
            try
            {
                foreach (var (hive, key, name) in new[]
                {
                    (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                    (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                    (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                })
                {
                    using RegistryKey? k = hive.OpenSubKey(key);
                    if (k?.GetValue(name) is string p && Directory.Exists(p))
                        return p.Replace('/', '\\');
                }
            }
            catch (Exception e) when (e is IOException or System.Security.SecurityException
                                       or UnauthorizedAccessException)
            {
                // fall through
            }
            return null;
        }

        /// <summary>
        /// Subdirectories matching a pattern, or nothing at all. The candidate sweep walks volumes and
        /// install roots this tool does not own, where an unreadable or vanished directory is the
        /// normal case rather than an error worth reporting.
        /// </summary>
        private static string[] SafeDirectories(string dir, string pattern)
        {
            try
            {
                return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}
