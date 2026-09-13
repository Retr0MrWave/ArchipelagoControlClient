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
            "       Ap.Control dump-save [<path>]\n\n" +
            "With --server and --username the client connects on startup as before. Without them it\n" +
            "waits for the in-game Archipelago page to supply the details.\n\n" +
            "dump-save parses a save and prints the location checks it would report, without\n" +
            "connecting to anything. With no path it looks where the client would look.");
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
