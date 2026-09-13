using Ap.Control;
using Ap.Control.Models;
using Ap.Control.Ui;
using Ap.Control.Utils;
using Ap.Control.Utils.Interfaces;
using Ap.Control.Memory;
using Ap.Control.Memory.Mac;
using Ap.Control.SaveFile;
using Ap.Control.Utils.Save;

if (args.Length > 0 && args[0] == "dump-save") return DumpSave(args);
if (args.Length > 0 && args[0] == "probe-game") return ProbeGame(args);

return await RunClientAsync(args);

static async Task<int> RunClientAsync(string[] args)
{
    string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    string? server = Arg("--server");
    string? username = Arg("--username");
    string? password = Arg("--password");
    string? source = Arg("--source");
    string? savePath = Arg("--save");
    string? itemsPath = Arg("--items");
    string? portText = Arg("--ui-port");

    if (args.Contains("--help") || args.Contains("-h"))
    {
        Console.WriteLine(
            "Usage: Ap.Control [--server <url> --username <name> [--password <pass>]]\n" +
            "                  [--source memory|file] [--save <path>] [--items <apitems.json>]\n" +
            "                  [--ui-port <n>] [--no-ui]\n" +
            "       Ap.Control dump-save [<path>]\n" +
            "       Ap.Control probe-game [--layout [<rtti-name>]]\n\n" +
            "With --server and --username the client connects on startup as before. Without them it\n" +
            "waits for the in-game Archipelago page to supply the details.\n\n" +
            "dump-save parses a save and prints the location checks it would report, without\n" +
            "connecting to anything. With no path it looks where the client would look.\n\n" +
            "probe-game asks the running game what the client can see of it: the build, whether the\n" +
            "pump is ticking, and whether the player's inventory can be found. --layout adds what\n" +
            "the instances in memory say about where the class keeps its fields, for when the scan\n" +
            "finds objects but none of them the player's. macOS only.");
        return 0;
    }

    if (!int.TryParse(portText ?? UiBridge.DefaultPort.ToString(), out int uiPort))
    {
        Console.Error.WriteLine($"[ui] --ui-port must be a number, got '{portText}'");
        return 1;
    }

    // Which levers reach the game depends on the platform. On Windows the client drives the game
    // from outside, through process-memory APIs; on macOS it drives a helper dylib loaded inside
    // the game, because that is the only approach the platform's security model actually welcomes.
    // Everything above this line - the Archipelago session, the item map, the UI bridge, the save
    // parser - is the same code on both.
    var shim = OperatingSystem.IsMacOS() ? new ShimClient() : null;

    await using IItemGranter granter = shim is null
        ? new NativeItemGranter()
        : new MacItemGranter(shim);
    await using IAbilityGranter abilityGranter = shim is null
        ? new NativeAbilityGranter()
        : new MacAbilityGranter(shim);
    using IDisposable gameflowLifetime = shim is null
        ? new NativeGameFlowController()
        : new MacGameFlowController(shim);
    var gameflow = (IGameFlowController)gameflowLifetime;

    ApItemMap itemMap;
    try
    {
        string mapPath = itemsPath ?? Path.Combine(AppContext.BaseDirectory, "apitems.json");
        string mapSource;
        if (File.Exists(mapPath))
        {
            itemMap = ApItemMap.Load(mapPath);
            mapSource = mapPath;
        }
        else if (EmbeddedItemMap() is { } embedded)
        {
            itemMap = ApItemMap.Parse(embedded);
            mapSource = "built-in copy";
        }
        else
        {
            itemMap = ApItemMap.Empty;
            mapSource = "none";
        }

        Console.WriteLine(itemMap.IsEmpty
            ? "Item map: none — clearance items still resolve (built in); everything else falls back to inventory GID (pass --items <path> to route sectors/keys)."
            : $"Item map: {itemMap.Count} entries loaded from {mapSource} (clearance items resolve built-in).");
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"[item-map] failed to load: {e.Message}");
        return 1;
    }
    Console.WriteLine(shim is null
        ? GameBuildRegistry.StartupBanner()
        : MacGameBuildRegistry.Describe(shim.EnsureConnected() ? shim.Hello() : null));

    var relay = new SaveNotifierRelay();
    using var session = new ApSessionHost(granter, abilityGranter, gameflow, itemMap, relay);

    UiBridge? bridge = null;
    if (!args.Contains("--no-ui"))
    {
        try
        {
            var candidate = new UiBridge(uiPort);
            candidate.ConnectRequested += session.ConnectAsync;
            candidate.StatusRequested += session.RefreshAsync;
            candidate.Start();

            session.StatusChanged += candidate.PushAsync;
            bridge = candidate;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[ui] bridge unavailable: {e.Message}");
            bridge = null;
        }
    }

    try
    {
        if (server is not null && username is not null)
        {
            await session.ConnectAsync(new ConnectRequest(server, "", username, password)).ConfigureAwait(false);
            if (session.Status.Status.StartsWith("failed", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[failed] {session.Status.Status}");
                if (bridge is null) return 1;
            }
        }
        else if (bridge is null)
        {
            Console.Error.WriteLine(
                "Nothing to do: no --server/--username given and the UI bridge is not running.");
            return 1;
        }
        else
        {
            Console.WriteLine("Waiting for the in-game Archipelago page to supply connection details...");
        }

        // The memory-backed watcher is a Windows implementation (it opens another process). On
        // macOS the save is read from disk, which is where it wants to be read from anyway - the
        // shim only has to say when to look.
        bool useFile = string.Equals(source, "file", StringComparison.OrdinalIgnoreCase)
                       || (source is null && OperatingSystem.IsMacOS());

        ISaveWatcher watcher;
        if (useFile)
        {
            string path = savePath
                ?? (OperatingSystem.IsMacOS()
                    ? MacSaveLocations.Preferred()
                    : Path.Combine(AppContext.BaseDirectory, "samples", "persistent.chunk"));

            var fileWatcher = new SaveFileWatcher(path);
            watcher = fileWatcher;

            Console.WriteLine(File.Exists(path)
                ? $"Save source: file ({path})"
                : $"Save source: file ({path}) — not there yet; it will be picked up when the game "
                  + "first saves. Pass --save if yours lives elsewhere.");

            // The shim sees the game call saveGame, which beats waiting for the filesystem to
            // notice and beats the fixed poll the Windows client falls back on.
            if (shim is not null) shim.SaveRequested += fileWatcher.RequestRescan;
        }
        else
        {
            watcher = new SaveMemoryWatcher();
            Console.WriteLine("Save source: process memory (Control_DX12)");
        }

        using (watcher)
        {
            watcher.Error += (_, e) => Console.Error.WriteLine($"[save-watch] {e.Exception.Message}");
            watcher.AddNotifier(relay);
            await watcher.StartAsync(emitInitial: true);

            using var quit = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
            try { await Task.Delay(Timeout.Infinite, quit.Token); }
            catch (OperationCanceledException) { /* Ctrl+C */ }
        }
        return 0;
    }
    finally
    {
        if (bridge is not null) await bridge.DisposeAsync();
    }
}

/// <summary>
/// Parse a save and print the location checks it would report, connecting to nothing.
///
/// The checks a player sees come from diffing consecutive saves, which makes "why did nothing
/// happen?" hard to answer: it could be the file, the parse, the diff, or the server. This runs
/// the first three against a save on disk and prints the result, so the rest is either confirmed
/// or ruled out in one command. Diffing against null is exactly what the client does on startup,
/// so this prints the same set the client would send at that moment.
/// </summary>
static int DumpSave(string[] args)
{
    string? path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
    path ??= OperatingSystem.IsMacOS() ? MacSaveLocations.Preferred() : null;

    if (path is null)
    {
        Console.Error.WriteLine("dump-save: no save path given and no default for this platform.");
        return 1;
    }
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"dump-save: no save at {path}");
        return 1;
    }

    ControlSave save;
    try
    {
        save = new ControlSaveParser().ParseFile(path);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"dump-save: {path} did not parse — {e.Message}");
        return 1;
    }

    Console.WriteLine($"Save: {path} ({new FileInfo(path).Length:N0} bytes)");
    Console.WriteLine($"Scope '{save.Header.FilenameStr}', {save.Chunks.Count} chunks: "
        + string.Join(", ", save.Chunks.Select(c => c.UidHigh.ToString())));

    SaveDiff diff = SaveDiffer.Diff(null, save);

    void Report(string what, IReadOnlyList<ulong> gids)
    {
        Console.WriteLine($"\n{what}: {gids.Count}");
        foreach (ulong gid in gids) Console.WriteLine($"  {gid}");
    }

    Report("Found locations", diff.NewFoundLocations);
    Report("Sectors visited", diff.NewSectorsVisited);
    Report("Control points", diff.NewUnlockedControlPoints);
    Report("Collectibles", diff.NewCollectibles);

    // Only state 2 is a completion, and only completions become checks.
    ulong[] completed = [.. diff.MissionChanges.Where(m => m.NewState == 2).Select(m => m.GidMissionId)];
    Report("Missions completed", completed);

    int total = diff.NewFoundLocations.Count + diff.NewSectorsVisited.Count
              + diff.NewUnlockedControlPoints.Count + diff.NewCollectibles.Count + completed.Length;
    Console.WriteLine($"\n{total} check(s) would be sent. Whether the server accepts each one "
        + "depends on the generated world having that location id.");
    return 0;
}

/// <summary>
/// Ask the running game what the client can see of it, and print the answer.
///
/// The macOS granters fail with a sentence rather than a stack trace — "this build is not mapped",
/// "is a save loaded?" — which is right for a player mid-session and useless for working out which
/// of the several things behind that sentence is actually the problem. This runs each step in
/// order and prints what it got: the shim answering, the build identified, the RTTI walk finding a
/// vtable, the heap sweep finding objects, the player flag narrowing them, the network role
/// picking one. Whichever line stops being plausible is the one to look at.
///
/// It is also how the inventory scan is confirmed to survive a save load: run it, load a save, run
/// it again.
/// </summary>
static int ProbeGame(string[] args)
{
    if (!OperatingSystem.IsMacOS())
    {
        Console.Error.WriteLine(
            "probe-game talks to the macOS helper dylib; this platform drives the game directly.");
        return 1;
    }

    using var shim = new ShimClient();
    if (!shim.EnsureConnected() || shim.Hello() is not { } hello)
    {
        Console.Error.WriteLine($"probe-game: nothing is listening on {shim.SocketPath}.");
        Console.Error.WriteLine(
            "Start Control with the Archipelago launch option set and try again. If it is running, "
            + "check ~/Library/Logs/Ap.Control/shim.log to see whether the dylib loaded.");
        return 1;
    }

    Console.WriteLine($"Shim:  connected on {shim.SocketPath} (game pid {hello.Pid})");
    Console.WriteLine(hello.PumpTicking
        ? $"Pump:  ticking, {hello.Beats:N0} frame(s) so far"
        : $"Pump:  not ticking, {hello.Beats:N0} frame(s) so far — at a menu or paused, so anything "
          + "that has to run on the game's own thread will wait for gameplay");
    if (hello.Executable is { } game)
        Console.WriteLine($"Image: {game.Name} {game.Uuid} loaded at 0x{game.Base:x}");
    Console.WriteLine(MacGameBuildRegistry.Describe(hello));

    // The RTTI walk. Both classes are reported because the second is what the ability grants will
    // need in Phase 3, and a build that renamed either is worth knowing about now.
    Console.WriteLine();
    foreach (string rttiName in new[] { MacPlayerInventory.RttiName, "30PlayerPropertiesComponentState" })
    {
        ShimVtableLookup lookup = shim.Vtables(rttiName);
        Console.WriteLine(lookup.Error is { } error
            ? $"RTTI {rttiName}: {error}"
            : $"RTTI {rttiName}: " + string.Join(", ", lookup.Vtables.Select(v =>
                $"0x{v.Address:x}{(v.IsPrimary ? " (primary)" : $" (offset_to_top {v.OffsetToTop})")}")));
    }

    InventoryLayout layout = MacGameBuildRegistry.Resolve(hello)?.Inventory ?? InventoryLayout.Default;
    var inventory = new MacPlayerInventory(shim);

    var clock = System.Diagnostics.Stopwatch.StartNew();
    MacPlayerInventory.Survey survey = inventory.Scan(layout);
    clock.Stop();

    Console.WriteLine();
    Console.WriteLine($"Inventory scan ({clock.ElapsedMilliseconds:N0} ms): {survey.Instances} object(s) "
        + $"of that class in memory, {survey.PlayerFlagged} flagged as the player's, "
        + $"{survey.Candidates.Count} with a readable network role");

    foreach (MacPlayerInventory.Candidate candidate in survey.Candidates)
        Console.WriteLine($"  0x{candidate.Address:x}  role {candidate.Role} "
            + $"({(candidate.Role == 3 ? "authoritative" : "replica")})  "
            + (candidate.Items is { } items ? $"{items} item(s)" : "item count not mapped")
            + (candidate.Address == survey.Chosen ? "   <- chosen" : ""));

    if (survey.Problem is { } problem) Console.WriteLine($"  {problem}");

    if (args.Contains("--layout"))
    {
        string rttiName = args.SkipWhile(a => a != "--layout").Skip(1)
            .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? MacPlayerInventory.RttiName;
        ReportLayout(shim, rttiName, layout);
    }

    if (survey.Chosen == 0) return 1;

    // The cheap re-check every grant would do before using the cached address.
    long again = inventory.Locate(layout);
    Console.WriteLine(again == survey.Chosen
        ? "  re-checked: the chosen object still looks like the player's inventory."
        : $"  re-checked: it no longer does; a fresh sweep chose 0x{again:x}.");
    return 0;
}

/// <summary>
/// Print what the instances in memory say about where a class keeps its fields.
///
/// Reached with <c>probe-game --layout [rtti-name]</c>, and worth reaching for when the scan finds
/// plenty of objects but none that look like the player's — which means the offsets it is reading
/// are not where this build put them.
/// </summary>
static void ReportLayout(ShimClient shim, string rttiName, InventoryLayout layout)
{
    const int Window = 0x200;

    Console.WriteLine();
    Console.WriteLine($"Layout probe: {rttiName}, first 0x{Window:x} bytes of each instance");

    MacLayoutProbe.Report report = MacLayoutProbe.Run(shim, rttiName, Window, layout);
    if (report.Problem is { } problem)
    {
        Console.WriteLine($"  {problem}");
        return;
    }

    Console.WriteLine($"  compared {report.Sampled} of {report.Instances} instance(s)");

    Console.WriteLine();
    Console.WriteLine("  Bytes that single out a few objects from the rest — the player flag should");
    Console.WriteLine("  be one of these, held by one object per network replica:");
    if (report.Flags.Count == 0)
    {
        Console.WriteLine("    none. Either no instance belongs to the player, or the flag is wider");
        Console.WriteLine("    than a byte, or it sits beyond the window.");
    }
    foreach (MacLayoutProbe.FlagCandidate flag in report.Flags.Take(12))
        Console.WriteLine($"    +0x{flag.Offset:x3}  0x{flag.Minority:x2} on {flag.Objects.Count} "
            + $"object(s), 0x{flag.Majority:x2} on the rest   "
            + string.Join(", ", flag.Objects.Take(4).Select(a => $"0x{a:x}"))
            + (flag.Objects.Count > 4 ? ", …" : "")
            + (flag.Offset == layout.IsPlayer ? "   <- currently read as IsPlayer" : ""));
    if (report.Flags.Count > 12)
        Console.WriteLine($"    … and {report.Flags.Count - 12} more");

    Console.WriteLine();
    Console.WriteLine("  Words whose top two bits read as a network role on nearly every object,");
    Console.WriteLine("  with both replica roles present:");
    if (report.Roles.Count == 0)
        Console.WriteLine("    none within the window.");
    foreach (MacLayoutProbe.RoleCandidate role in report.Roles.Take(12))
        Console.WriteLine($"    +0x{role.Offset:x3}  roles {{{string.Join(",", role.Roles)}}} on "
            + $"{role.Matched}/{role.Total}"
            + (role.Offset == layout.NetRole ? "   <- currently read as NetRole" : ""));

    Console.WriteLine();
    Console.WriteLine("  Words the player's own objects agree on, small enough to be a count of");
    Console.WriteLine($"  items — one of these is ItemCount, currently unmapped:");
    if (report.Counts.Count == 0)
        Console.WriteLine("    none. Needs the player flag mapped first, and two replicas to compare.");
    foreach (MacLayoutProbe.CountCandidate count in report.Counts.Take(16))
        Console.WriteLine($"    +0x{count.Offset:x3}  {count.Value}");
    if (report.Counts.Count > 16)
        Console.WriteLine($"    … and {report.Counts.Count - 16} more");
    if (report.Counts.Count > 0)
    {
        Console.WriteLine("    To tell them apart, note which value matches your inventory, pick an");
        Console.WriteLine("    item up, and run this again: the count is the one that went up by one.");
    }

    Console.WriteLine();
    Console.WriteLine("  An offset that appears in both of the first two lists, or a flag offset whose");
    Console.WriteLine("  objects are a subset of one role offset's, is the pair for InventoryLayout.");
}

/// <summary>
/// The apitems.json embedded at build time, or null if this build has none. Named explicitly via
/// LogicalName in the .csproj so the lookup does not depend on the root namespace.
/// </summary>
static string? EmbeddedItemMap()
{
    using Stream? s = System.Reflection.Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("apitems.json");
    if (s is null) return null;
    using var reader = new StreamReader(s);
    return reader.ReadToEnd();
}
