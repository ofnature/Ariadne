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
    private readonly Func<IReadOnlyCollection<uint>> _attunedIds;

    /// <param name="blocked">Teleporting is impossible or unwanted right now: in a duty, in
    /// combat, on a quest vehicle, between areas.</param>
    /// <param name="attunedIds">The character's attuned aetheryte ids, read live.</param>
    public TeleportService(LifestreamIpc lifestream, AetheryteCatalog catalog, AriadneConfig config,
        Func<uint> territory, Func<bool> blocked, Func<IReadOnlyCollection<uint>> attunedIds)
    {
        _lifestream = lifestream;
        _catalog = catalog;
        _config = config;
        _territory = territory;
        _blocked = blocked;
        _attunedIds = attunedIds;
    }

    public Plan? TryPlan(Vector3 player, Vector3 goal, bool fly)
    {
        if (_blocked() || !_lifestream.IsAvailable)
            return null;
        var attuned = new HashSet<uint>(_attunedIds());
        var candidates = new List<(uint, Vector3)>();
        var names = new Dictionary<uint, string>();
        foreach (var e in _catalog.InTerritory(_territory()))
        {
            if (!attuned.Contains(e.Id))
                continue;
            candidates.Add((e.Id, e.Position));
            names[e.Id] = e.Name;
        }
        var choice = TeleportPlanner.Choose(player, goal, fly, candidates, _config.TeleportCostSeconds, _config.TeleportMinSavingSeconds);
        return choice == null ? null : new Plan(choice.AetheryteId, names[choice.AetheryteId], choice.Position, choice.DirectSeconds, choice.ViaSeconds);
    }

    public bool Start(uint aetheryteId) => _lifestream.Teleport(aetheryteId);
    public bool Busy => _lifestream.IsBusy;
    public void Abort() => _lifestream.Abort();
}
