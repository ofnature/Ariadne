using Ariadne.Config;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Travel;

/// <summary>Glue between the planner rule, the catalog, the character's attunements and
/// Lifestream. Same-zone only: a goal is a point in the current territory by definition.</summary>
internal sealed class TeleportService
{
    public sealed record Plan(uint AetheryteId, string Name, Vector3 Position, float DirectSeconds, float ViaSeconds);

    private readonly LifestreamIpc _lifestream;
    private readonly AetheryteCatalog _catalog;
    private readonly AriadneConfig _config;
    private readonly Func<uint> _territory;
    private readonly Func<bool> _blocked;
    private readonly Func<bool> _inTransit;
    private readonly Func<IReadOnlyCollection<uint>> _attunedIds;

    /// <param name="blocked">Teleporting is impossible or unwanted right now: in a duty, in
    /// combat, on a quest vehicle, between areas.</param>
    /// <param name="inTransit">The game says a teleport is under way: casting, or between
    /// areas. Lifestream's own busy flag covers only its queue — it hands the cast to the game
    /// and reports idle while the cast runs (found in the field 2026-09-13).</param>
    /// <param name="attunedIds">The character's attuned aetheryte ids, read live.</param>
    public TeleportService(LifestreamIpc lifestream, AetheryteCatalog catalog, AriadneConfig config,
        Func<uint> territory, Func<bool> blocked, Func<bool> inTransit, Func<IReadOnlyCollection<uint>> attunedIds)
    {
        _inTransit = inTransit;
        _lifestream = lifestream;
        _catalog = catalog;
        _config = config;
        _territory = territory;
        _blocked = blocked;
        _attunedIds = attunedIds;
    }

    /// <param name="why">Why no leg was planned — logged, so a silent walk is never a mystery.</param>
    public Plan? TryPlan(Vector3 player, Vector3 goal, bool fly, out string why)
    {
        why = "";
        if (_blocked())
        {
            why = "blocked (duty, combat, quest vehicle, or between areas)";
            return null;
        }
        if (!_lifestream.IsAvailable)
        {
            why = "Lifestream is not loaded";
            return null;
        }
        var attuned = new HashSet<uint>(_attunedIds());
        var candidates = new List<(uint, Vector3)>();
        var names = new Dictionary<uint, string>();
        var inZone = 0;
        foreach (var e in _catalog.InTerritory(_territory()))
        {
            inZone++;
            if (!attuned.Contains(e.Id))
                continue;
            candidates.Add((e.Id, e.Position));
            names[e.Id] = e.Name;
        }
        if (candidates.Count == 0)
        {
            why = $"no attuned aetheryte in territory {_territory()} ({inZone} in the catalog here, {attuned.Count} attuned anywhere)";
            return null;
        }
        var choice = TeleportPlanner.Choose(player, goal, fly, candidates, _config.TeleportCostSeconds, _config.TeleportMinSavingSeconds);
        if (choice == null)
        {
            var speed = fly ? TeleportPlanner.FlySpeed : TeleportPlanner.WalkSpeed;
            var direct = TeleportPlanner.Horizontal(player, goal) / speed;
            var best = float.MaxValue;
            var bestName = "";
            foreach (var (id, pos) in candidates)
            {
                var via = _config.TeleportCostSeconds + TeleportPlanner.Horizontal(pos, goal) / speed;
                if (via < best) { best = via; bestName = names[id]; }
            }
            why = $"direct {direct:0}s, best via {bestName} {best:0}s — needs {_config.TeleportMinSavingSeconds:0}s saving ({candidates.Count} attuned here)";
            return null;
        }
        return new Plan(choice.AetheryteId, names[choice.AetheryteId], choice.Position, choice.DirectSeconds, choice.ViaSeconds);
    }

    /// <summary>The zone's aetherytes as the planner sees them — for `/aria aetherytes`.</summary>
    public IEnumerable<string> Describe(Vector3 player)
    {
        yield return $"territory {_territory()}, Lifestream {(_lifestream.IsAvailable ? "loaded" : "NOT loaded")}, blocked={_blocked()}";
        var attuned = new HashSet<uint>(_attunedIds());
        yield return $"{attuned.Count} attuned aetherytes anywhere, {_catalog.Count} placed in the catalog";
        var any = false;
        foreach (var e in _catalog.InTerritory(_territory()))
        {
            any = true;
            yield return $"  #{e.Id} {e.Name} at {e.Position:f1} — {TeleportPlanner.Horizontal(player, e.Position):0}y away (horizontal), {(attuned.Contains(e.Id) ? "attuned" : "NOT attuned")}";
        }
        if (!any)
            yield return "  (no aetheryte placed in this territory)";
    }

    public bool Start(uint aetheryteId) => _lifestream.Teleport(aetheryteId);
    /// <summary>A teleport is in progress: Lifestream working the queue, or the game casting / loading.</summary>
    public bool Busy => _lifestream.IsBusy || _inTransit();
    public void Abort() => _lifestream.Abort();
}
