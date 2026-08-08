using Ariadne.Config;
using Ariadne.Ipc;
using Ariadne.Zone;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Ariadne.Windows;

/// <summary>
/// The whole bridge on one screen: Mnemosyne connection, current zone and its mesh status,
/// vnavmesh's view of the world, manual overrides for everything the broker does
/// automatically, and the recent-activity trail that explains what just happened.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Vector4 Green = new(0.3f, 0.9f, 0.3f, 1);
    private static readonly Vector4 Red = new(0.9f, 0.35f, 0.35f, 1);
    private static readonly Vector4 Yellow = new(0.95f, 0.85f, 0.35f, 1);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1);

    private readonly AriadneConfig _config;
    private readonly Action _saveConfig;
    private readonly MeshBroker _broker;
    private readonly VnavIpc _vnav;
    private readonly ZoneWatcher _zoneWatcher;
    private readonly Func<Vector3?> _playerPosition;

    private Vector3 _pathDest;
    private bool _pathFly;
    private string _lastPathResult = "";

    public MainWindow(
        AriadneConfig config,
        Action saveConfig,
        MeshBroker broker,
        VnavIpc vnav,
        ZoneWatcher zoneWatcher,
        Func<Vector3?> playerPosition)
        : base("Ariadne##AriadneMain", ImGuiWindowFlags.NoCollapse)
    {
        _config = config;
        _saveConfig = saveConfig;
        _broker = broker;
        _vnav = vnav;
        _zoneWatcher = zoneWatcher;
        _playerPosition = playerPosition;

        Size = new Vector2(560, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(1200, 1000),
        };
    }

    public override void Draw()
    {
        DrawMnemosyne();
        DrawZone();
        DrawVnavmesh();
        DrawActions();
        DrawActivity();
    }

    private void DrawMnemosyne()
    {
        ImGui.TextUnformatted("Mnemosyne");
        ImGui.SameLine();
        if (_broker.MnemosyneConnected)
            ImGui.TextColored(Green, $"connected — {_broker.MnemosyneApp}");
        else
            ImGui.TextColored(Red, "disconnected (start Mnemosyne or the stub; reconnects automatically)");
        ImGui.Separator();
    }

    private void DrawZone()
    {
        var snapshot = _broker.Current;
        ImGui.TextUnformatted("Zone");
        ImGui.SameLine();
        var (color, label) = snapshot.Status switch
        {
            ZoneMeshStatus.NotReady => (Grey, "not ready (loading)"),
            ZoneMeshStatus.LocalCurrent => (Green, "cached locally — vnavmesh will skip its build"),
            ZoneMeshStatus.MnemosyneCached => (Yellow, "cached in Mnemosyne — seed to hand it to vnavmesh"),
            ZoneMeshStatus.MnemosyneUnavailable => (Grey, "unknown — Mnemosyne unreachable"),
            ZoneMeshStatus.Missing => (Red, "missing everywhere — vnavmesh will build from scratch"),
            _ => (Grey, snapshot.Status.ToString()),
        };
        ImGui.TextColored(color, label);

        DrawKeyValue("cache key", _zoneWatcher.CurrentCacheKey);
        DrawKeyValue("layout key", _zoneWatcher.CurrentKey);
        if (snapshot.MeshPath is { } path)
            DrawKeyValue("mesh file", path);
        ImGui.Separator();
    }

    private void DrawVnavmesh()
    {
        ImGui.TextUnformatted("vnavmesh");
        ImGui.SameLine();
        if (!_vnav.IsAvailable)
        {
            ImGui.TextColored(Grey, "not loaded");
        }
        else if (_vnav.IsReady)
        {
            ImGui.TextColored(Green, "mesh ready");
        }
        else
        {
            var progress = _vnav.BuildProgress;
            if (progress >= 0)
            {
                ImGui.TextColored(Yellow, "building");
                ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{progress * 100:0}%");
            }
            else
            {
                ImGui.TextColored(Grey, "no mesh loaded");
            }
        }
        ImGui.Separator();
    }

    private void DrawActions()
    {
        var autoSeed = _config.AutoSeed;
        if (ImGui.Checkbox("Auto-seed vnavmesh cache on zone load", ref autoSeed))
        {
            _config.AutoSeed = autoSeed;
            _saveConfig();
        }

        if (ImGui.Button("Refresh"))
            _ = _broker.RefreshAsync();
        ImGui.SameLine();
        if (ImGui.Button("Seed vnavmesh cache"))
            _ = _broker.SeedVnavCacheAsync();
        ImGui.SameLine();
        if (ImGui.Button("Reload vnavmesh"))
            _vnav.Reload();

        // findPath smoke test — expected to fail against the stub, works once real Mnemosyne lands
        ImGui.SetNextItemWidth(240);
        ImGui.InputFloat3("dest", ref _pathDest);
        ImGui.SameLine();
        ImGui.Checkbox("fly", ref _pathFly);
        ImGui.SameLine();
        if (ImGui.Button("FindPath from player"))
        {
            if (_playerPosition() is { } from)
            {
                var dest = _pathDest;
                var fly = _pathFly;
                _ = Task.Run(async () =>
                {
                    var path = await _broker.FindPathAsync(from, dest, fly);
                    _lastPathResult = path.Count > 0 ? $"{path.Count} waypoints" : "no path (see activity)";
                });
            }
            else
            {
                _lastPathResult = "no player position";
            }
        }
        if (_lastPathResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Grey, _lastPathResult);
        }
        ImGui.Separator();
    }

    private void DrawActivity()
    {
        ImGui.TextUnformatted("Activity");
        if (ImGui.BeginChild("##activity", new Vector2(-1, -1), true))
        {
            var entries = _broker.RecentActivity;
            for (var i = entries.Length - 1; i >= 0; i--)
                ImGui.TextUnformatted(entries[i]);
        }
        ImGui.EndChild();
    }

    private static void DrawKeyValue(string label, string value)
    {
        ImGui.TextColored(Grey, label);
        ImGui.SameLine(90);
        ImGui.TextUnformatted(value.Length > 0 ? value : "—");
        if (value.Length > 0 && ImGui.IsItemClicked())
            ImGui.SetClipboardText(value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("click to copy");
    }
}
