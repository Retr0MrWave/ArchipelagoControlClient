using Ap.Control;
using Ap.Control.Models;
using Ap.Control.Ui;
using Ap.Control.Utils;
using Ap.Control.Utils.Interfaces;
using Ap.Control.Memory;
using Ap.Control.Memory.Mac;
using Ap.Control.SaveFile;

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
            "                  [--ui-port <n>] [--no-ui]\n\n" +
            "With --server and --username the client connects on startup as before. Without them it\n" +
            "waits for the in-game Archipelago page to supply the details.");
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
