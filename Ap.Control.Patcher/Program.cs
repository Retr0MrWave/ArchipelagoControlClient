using System.Diagnostics;

namespace Ap.Control.Patcher
{
    /// <summary>
    /// Applies the Ap.Control UI patches to a Control install.
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// The running game, as the process list names it: Control_DX12.exe on Windows, and the
        /// bundle's CFBundleExecutable — plain "Game" — on macOS.
        /// </summary>
        private static readonly string GameProcess = OperatingSystem.IsMacOS() ? "Game" : "Control_DX12";

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (PatchException e)
            {
                // Expected, explainable failures: report the message, not a stack trace.
                Console.Error.WriteLine($"error: {e.Message}");
                return 1;
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    "error: access denied writing to the game folder.\n" +
                    (OperatingSystem.IsMacOS()
                        ? "Close Steam and the game. If it persists, check that your user owns the\n" +
                          "Steam library folder — a library on an external disk is often read-only."
                        : "Close Steam and the game, or run this from an elevated prompt."));
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            var cli = CommandLine.Parse(args);

            string? command = cli.Positionals.FirstOrDefault();
            if (command is null or "-h" or "--help" or "help") return Usage();

            var store = new BackupStore(cli.Get("--backup-dir"));
            bool dryRun = cli.HasFlag("--dry-run");
            string? gameOpt = cli.Get("--game");

            // "all" (or nothing) selects every patch; otherwise the second positional names one.
            string selector = cli.Positionals.Skip(1).FirstOrDefault() ?? "all";
            IReadOnlyList<PatchDef> selected =
                selector.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? Patches.All
                    : Patches.ById(selector) is { } one
                        ? [one]
                        : throw new PatchException(
                            $"unknown patch '{selector}'. Known: "
                            + $"{string.Join(", ", Patches.All.Concat(Patches.Diagnostics).Select(p => p.Id))}, all");

            string game = GameLocator.Resolve(gameOpt);
            Console.WriteLine($"game      : {game}");

            return command.ToLowerInvariant() switch
            {
                "status" => ForEach(selected, p => Status(p, game, store)),
                "apply" => ForEach(selected, p => Apply(p, game, store, dryRun, cli.Get("--out"))),
                "verify" => ForEach(selected, p => Verify(p, game)),
                "restore" => ForEach(selected, p => Restore(p, game, store)),
                _ => Usage(),
            };
        }

        /// <summary>Run an action over each selected patch; the worst exit code wins.</summary>
        private static int ForEach(IReadOnlyList<PatchDef> patches, Func<PatchDef, int> action)
        {
            int worst = 0;
            foreach (var patch in patches)
            {
                Console.WriteLine();
                Console.WriteLine($"=== {patch.Title} [{patch.Id}] ===");
                try
                {
                    worst = Math.Max(worst, action(patch));
                }
                catch (PatchException e)
                {
                    Console.Error.WriteLine($"error: {e.Message}");
                    worst = 1;
                }
            }
            return worst;
        }

        private static (PackFile.Located Pack, byte[] Blob) Load(PatchDef patch, string game)
        {
            PackFile.Located pack = PackFile.Locate(
                Path.Combine(game, "data_packfiles"), patch.Package, patch.Target);
            return (pack, PackFile.ReadBlob(pack.Rmdp, pack.Entry));
        }

        private static int Status(PatchDef patch, string game, BackupStore store)
        {
            var (pack, blob) = Load(patch, game);
            PackFile.Entry entry = pack.Entry;

            Console.WriteLine($"target    : {patch.Target} in {Path.GetFileName(pack.Bin)}");
            Console.WriteLine($"content   : offset 0x{entry.Offset:X}  length {entry.Length}");
            Console.WriteLine($"sha1      : {BackupStore.Sha1(blob)}");
            Console.WriteLine($"rmdp size : {new FileInfo(pack.Rmdp).Length:N0}");
            Console.WriteLine($"state     : {Describe(patch.StateOf(blob))}");

            if (store.Has(patch) && store.ReadManifest(patch) is { } m)
            {
                Console.WriteLine($"backup    : present ({store.BlobSize(patch):N0} bytes, patched [{string.Join(", ", m.Applied)}])");
                Console.WriteLine($"            offset/length unchanged since patch: "
                    + $"{entry.Offset == m.Offset && entry.Length == m.Length}");
                Console.WriteLine($"            at {store.DirFor(patch)}");
            }
            else
            {
                Console.WriteLine("backup    : none");
            }
            return 0;
        }

        private static string Describe(PatchState state) => state switch
        {
            PatchState.Patched => "PATCHED",
            PatchState.Stock => "STOCK",
            _ => "UNKNOWN (neither stock nor patched anchors present — game updated?)",
        };

        private static int Apply(PatchDef patch, string game, BackupStore store, bool dryRun, string? cliOut)
        {
            var (pack, blob) = Load(patch, game);
            (string rmdp, PackFile.Entry entry) = (pack.Rmdp, pack.Entry);

            if (patch.StateOf(blob) == PatchState.Patched)
            {
                Console.WriteLine("already patched — nothing to do.");
                return 0;
            }

            byte[] patched = patch.Build(blob);
            Console.WriteLine($"edits     : {string.Join(", ", patch.Edits.Select(e => e.Label))}");
            Console.WriteLine($"length    : {blob.Length} -> {patched.Length}  (NEUTRAL — no .bin or .packmeta change)");
            Console.WriteLine($"write     : in place at 0x{entry.Offset:X} in {Path.GetFileName(rmdp)}");

            if (dryRun)
            {
                // Writing the result out lets the JS be syntax-checked before anything touches the
                // game archive — cheaper than applying, launching, and finding a blank screen.
                if (cliOut is { } outPath)
                {
                    File.WriteAllBytes(outPath, patched);
                    Console.WriteLine($"--dry-run: patched content written to {outPath}");
                }
                Console.WriteLine("--dry-run: game files untouched.");
                return 0;
            }

            if (GameIsRunning())
                throw new PatchException(
                    $"{GameProcess} is running — close the game (and Steam, which also locks the "
                    + "archive) before patching.");

            if (store.Save(patch, blob, patched, entry))
                Console.WriteLine($"backed up original {patch.Target} -> {store.DirFor(patch)}");

            PackFile.WriteBlob(rmdp, entry, patched);

            byte[] check = PackFile.ReadBlob(rmdp, entry);
            bool ok = check.AsSpan().SequenceEqual(patched);
            Console.WriteLine($"applied. readback matches: {ok}");
            return ok ? 0 : 1;
        }

        private static int Verify(PatchDef patch, string game)
        {
            var (pack, blob) = Load(patch, game);

            string[] missing = [.. patch.Edits.Where(e => Bytes.Count(blob, e.New) != 1).Select(e => e.Label)];
            if (missing.Length > 0)
            {
                Console.WriteLine($"NOT patched (missing: {string.Join(", ", missing)})");
                return 1;
            }
            Console.WriteLine($"verified: all {patch.Edits.Count} edit(s) present, length {pack.Entry.Length} (unchanged)");
            return 0;
        }

        private static int Restore(PatchDef patch, string game, BackupStore store)
        {
            if (!store.Has(patch) || store.ReadManifest(patch) is not { } m)
            {
                Console.WriteLine($"no backup — nothing to restore (looked in {store.DirFor(patch)}).");
                return 1;
            }

            if (GameIsRunning())
                throw new PatchException(
                    $"{GameProcess} is running — close the game (and Steam) before restoring.");

            var (pack, _) = Load(patch, game);
            (string rmdp, PackFile.Entry entry) = (pack.Rmdp, pack.Entry);
            if (entry.Offset != m.Offset || entry.Length != m.Length)
                throw new PatchException(
                    "the package index no longer matches the backup (offset/length changed — game "
                    + "updated?). Use Steam's 'verify integrity of game files' instead.");

            byte[] original = store.ReadBlob(patch);
            PackFile.WriteBlob(rmdp, entry, original);

            byte[] back = PackFile.ReadBlob(rmdp, entry);
            bool ok = BackupStore.Sha1(back) == m.OrigSha1;
            Console.WriteLine($"restored {original.Length:N0} bytes at 0x{m.Offset:X}; sha1 matches original: {ok}");
            return ok ? 0 : 1;
        }

        private static bool GameIsRunning() => Process.GetProcessesByName(GameProcess).Length > 0;

        /// <summary>
        /// Splits argv into positionals and options.
        /// </summary>
        private sealed record CommandLine(List<string> Positionals, Dictionary<string, string?> Options)
        {
            private static readonly string[] TakesValue = ["--game", "--backup-dir", "--out"];

            internal static CommandLine Parse(string[] args)
            {
                var positionals = new List<string>();
                var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (!a.StartsWith('-')) { positionals.Add(a); continue; }

                    if (TakesValue.Contains(a, StringComparer.OrdinalIgnoreCase))
                    {
                        if (i + 1 >= args.Length)
                            throw new PatchException($"{a} needs a value");
                        options[a] = args[++i];
                    }
                    else
                    {
                        options[a] = null;   // bare flag
                    }
                }
                return new CommandLine(positionals, options);
            }

            internal string? Get(string name) => Options.GetValueOrDefault(name);
            internal bool HasFlag(string name) => Options.ContainsKey(name);
        }

        private static int Usage()
        {
            Console.WriteLine("""
                Ap.Control patcher — applies the Archipelago UI patches to a Control install.

                USAGE
                  Ap.Control.Patcher <command> [patch] [options]

                COMMANDS
                  status    Show whether each patch is applied, and whether a backup exists
                  apply     Apply the patch (backs up the original first)
                  verify    Exit non-zero unless the patch is present
                  restore   Put back the original bytes from the backup

                PATCH
                  elevator  Gate each elevator sector destination independently
                  shop      Remove weapon-form unlocks from the control-point shop
                  abilities Lock every not-yet-owned node in the Abilities menu
                  bootstrap Let the client serve the in-game Archipelago UI over 127.0.0.1
                  all       Every gameplay patch (default)

                OPTIONS
                  --game <path>        Control install folder (auto-detected if omitted). On macOS,
                                       the Steam "Control" folder, Game.app, or Game.app/Contents/
                                       Resources all work
                  --backup-dir <path>  Where originals are kept (default: Ap.Control in the user's
                                       local application data)
                  --dry-run            Show what apply would do, leave the game untouched
                  --out <path>         With --dry-run, save the patched content for inspection

                EXAMPLES
                  Ap.Control.Patcher status
                  Ap.Control.Patcher apply shop --dry-run
                  Ap.Control.Patcher apply all
                  Ap.Control.Patcher restore elevator

                NOTE
                  Close the game and Steam before applying — both lock the archive. Steam's "verify
                  integrity of game files" and game updates revert these patches.
                """);
            return 1;
        }
    }
}
