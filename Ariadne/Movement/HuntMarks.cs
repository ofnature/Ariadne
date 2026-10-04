using Ariadne.Config;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Ariadne.Movement;

/// <summary>
/// Keeps Ariadne's walking routes clear of hunt marks (B/A/S ranks) while a consumer holds it on:
/// Odysseus driving MSQ, side quests and aether currents asked for it (2026-10-02) so a quest run
/// never walks a character into an A rank.
///
/// <para>Which marks belong to a zone comes from the game: TerritoryType links each hunt zone to
/// its NotoriousMonsterTerritory row, which lists that zone's marks (47 zones). Spawn points are not
/// in the game files - the server places them - so marks are found live, as battle NPCs with one of
/// the zone's base ids. The list is read when the zone loads and dropped when it unloads.</para>
///
/// <para>It is a hold, not a setting: <see cref="Hold"/> runs for the seconds asked, per owner, and
/// lapses unless renewed, so a consumer that stops or crashes cannot leave every later route
/// avoiding marks.</para>
/// </summary>
internal sealed class HuntMarks
{
    private const float ScanRange = 150f;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);

    public readonly record struct Mark(Vector3 Center, float Radius, string Name);

    private readonly AriadneConfig _config;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _holds = [];
    private readonly HashSet<ulong> _announced = [];

    private uint _territory = uint.MaxValue;
    private Dictionary<uint, string> _zoneMarks = []; // base id -> "A Li'l Murderer"
    private DateTime _nextScan;

    /// <summary>Live marks near the player, as circles, while a hold runs; empty otherwise.
    /// Written on the framework thread, read from path requests on any thread.</summary>
    public volatile Mark[] Nearby = [];

    public HuntMarks(AriadneConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>Hold avoidance on for <paramref name="owner"/> for <paramref name="seconds"/>;
    /// 0 releases that owner's hold. Returns whether any hold is now running.</summary>
    public bool Hold(string owner, int seconds)
    {
        lock (_lock)
        {
            var wasActive = ActiveUnlocked(DateTime.UtcNow);
            if (seconds > 0)
                _holds[owner] = DateTime.UtcNow.AddSeconds(seconds);
            else
                _holds.Remove(owner);
            var active = ActiveUnlocked(DateTime.UtcNow);
            if (active != wasActive)
                _log(active ? $"[Hunt] avoiding hunt marks (held by {owner})" : "[Hunt] no longer avoiding hunt marks");
            return active;
        }
    }

    /// <summary>Who holds avoidance on right now, for the window.</summary>
    public string[] Holders
    {
        get
        {
            lock (_lock)
                return ActiveUnlocked(DateTime.UtcNow) ? [.. _holds.Keys] : [];
        }
    }

    /// <summary>How many hunt marks this zone has (0 outside hunt zones).</summary>
    public int ZoneMarkCount => _zoneMarks.Count;

    public bool Active
    {
        get
        {
            lock (_lock)
                return ActiveUnlocked(DateTime.UtcNow);
        }
    }

    private bool ActiveUnlocked(DateTime now)
    {
        foreach (var owner in _holds.Where(h => h.Value <= now).Select(h => h.Key).ToList())
            _holds.Remove(owner);
        return _holds.Count > 0;
    }

    /// <summary>Framework thread: follow the zone, and while a hold runs, find its marks.</summary>
    public void Tick(uint territory, Vector3? player)
    {
        if (territory != _territory)
        {
            _territory = territory;
            _zoneMarks = MarksFor(territory);
            _announced.Clear();
            Nearby = [];
        }
        var now = DateTime.UtcNow;
        if (_zoneMarks.Count == 0 || player is not { } here || !Active)
        {
            if (Nearby.Length > 0)
                Nearby = [];
            return;
        }
        if (now < _nextScan)
            return;
        _nextScan = now + ScanInterval;

        var found = new List<Mark>();
        foreach (var obj in Service.ObjectTable)
        {
            if (obj is not IBattleNpc npc || npc.CurrentHp == 0 || !_zoneMarks.TryGetValue(npc.BaseId, out var name))
                continue;
            if (Vector3.Distance(npc.Position, here) > ScanRange)
                continue;
            found.Add(new Mark(npc.Position, npc.HitboxRadius + _config.HuntMarkAvoidRadius, name));
            if (_announced.Add(npc.GameObjectId))
                _log($"[Hunt] {name} at {npc.Position:0} ({Vector3.Distance(npc.Position, here):0}y) - routes keep {npc.HitboxRadius + _config.HuntMarkAvoidRadius:0}y clear");
        }
        Nearby = [.. found];
    }

    private static Dictionary<uint, string> MarksFor(uint territory)
    {
        var marks = new Dictionary<uint, string>();
        if (Service.LuminaRow<TerritoryType>(territory) is not { } zone
            || Service.LuminaRow<NotoriousMonsterTerritory>(zone.NotoriousMonsterTerritory.RowId) is not { } list)
            return marks;
        foreach (var entry in list.NotoriousMonsters)
        {
            if (entry.RowId == 0 || Service.LuminaRow<NotoriousMonster>(entry.RowId) is not { } mark || mark.BNpcBase.RowId == 0)
                continue;
            var rank = mark.Rank switch { 1 => "B", 2 => "A", 3 => "S", _ => "?" };
            var name = Service.LuminaRow<BNpcName>(mark.BNpcName.RowId)?.Singular.ExtractText() ?? $"mark {mark.BNpcBase.RowId}";
            marks.TryAdd(mark.BNpcBase.RowId, $"{rank} {name}");
        }
        return marks;
    }

    /// <summary>Horizontal distance from a point to a segment - for "does the route still clear
    /// this mark".</summary>
    public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var abx = b.X - a.X;
        var abz = b.Z - a.Z;
        var lenSq = abx * abx + abz * abz;
        var t = lenSq < 1e-6f ? 0f : Math.Clamp(((p.X - a.X) * abx + (p.Z - a.Z) * abz) / lenSq, 0f, 1f);
        var dx = a.X + abx * t - p.X;
        var dz = a.Z + abz * t - p.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
