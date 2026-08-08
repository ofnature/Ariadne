using Ariadne.Mnemosyne;
using Ariadne.Seeding;
using Ariadne.Zone;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using static Ariadne.Service;

namespace Ariadne;

/// <summary>
/// Ariadne — bridge between the game session and Mnemosyne's out-of-process navmesh cache.
///
/// <para>Milestone 2: zone detection + Mnemosyne round-trip, log-only. Design and phased
/// scope live in <c>PLAN.md</c> at the repo root (local-only).</para>
/// </summary>
public sealed class AriadnePlugin : IDalamudPlugin
{
    private const string CommandMain = "/ariadne";

    public static string PluginVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly ZoneWatcher _zoneWatcher;
    private readonly MnemosyneClient _mnemosyne;

    public AriadnePlugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        _mnemosyne = new MnemosyneClient(m => Log.Information(m), m => Log.Warning(m));
        _zoneWatcher = new ZoneWatcher(Framework);
        _zoneWatcher.KeyChanged += OnKeyChanged;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Print the current zone layout key and mesh cache key.",
        });

        Log.Information($"Ariadne v{PluginVersion} loaded.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandMain);
        _zoneWatcher.KeyChanged -= OnKeyChanged;
        _zoneWatcher.Dispose();
        _mnemosyne.Dispose();
    }

    private void OnKeyChanged(string key)
    {
        if (key.Length == 0)
            return;
        var cacheKey = _zoneWatcher.CurrentCacheKey;
        _ = Task.Run(() => QueryMnemosyneAsync(cacheKey));
    }

    // Milestone 2: ask Mnemosyne about the zone and log the round-trip. Seeding comes later.
    private async Task QueryMnemosyneAsync(string cacheKey)
    {
        var sw = Stopwatch.StartNew();
        var status = await _mnemosyne.ZoneStatusAsync(cacheKey);
        if (status == null)
        {
            Log.Information($"[Mnemosyne] '{cacheKey}': unavailable ({sw.ElapsedMilliseconds}ms)");
            return;
        }

        Log.Information($"[Mnemosyne] '{cacheKey}': {status.Status} v{status.Version} cust{status.Customization} ({sw.Elapsed.TotalMilliseconds:0.0}ms)");
        if (status.Status != "cached")
            return;

        sw.Restart();
        var mesh = await _mnemosyne.GetMeshAsync(cacheKey);
        if (mesh is not { Ok: true } || mesh.Path == null)
        {
            Log.Information($"[Mnemosyne] getMesh failed: {mesh?.Error ?? "unavailable"}");
            return;
        }

        // never trust the server's word for it — the header is what vnavmesh will judge
        if (!NavmeshHeader.TryRead(mesh.Path, out var header) || !header.IsCurrent)
        {
            Log.Warning($"[Mnemosyne] getMesh returned unusable file (magic {header.Magic:X8}, v{header.Version}): '{mesh.Path}'");
            return;
        }

        Log.Information($"[Mnemosyne] getMesh: '{mesh.Path}' v{header.Version} cust{header.Customization} ({mesh.Size} bytes, {sw.Elapsed.TotalMilliseconds:0.0}ms)");
    }

    private void OnCommand(string command, string args)
    {
        var key = _zoneWatcher.CurrentKey;
        var cacheKey = _zoneWatcher.CurrentCacheKey;
        var mnemosyne = _mnemosyne.IsConnected ? $"connected ({_mnemosyne.ServerApp})" : "disconnected";
        ChatGui.Print(key.Length > 0
            ? $"[Ariadne] layout key: {key}\n[Ariadne] cache key: {cacheKey}\n[Ariadne] Mnemosyne: {mnemosyne}"
            : $"[Ariadne] layout not ready (loading, or watcher disabled — see /xllog).\n[Ariadne] Mnemosyne: {mnemosyne}");
        Log.Information($"[Command] key='{key}' cacheKey='{cacheKey}' mnemosyne={mnemosyne}");
    }
}
