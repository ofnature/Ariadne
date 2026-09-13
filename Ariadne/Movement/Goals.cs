using System;
using System.Numerics;

namespace Ariadne.Movement;

/// <summary>
/// Baritone-style goals: what "arrived" means, evaluated live every tick while following,
/// plus the point the planner aims at. The follower still ends a path on its own when the
/// waypoints run out; a goal can end it earlier (inside interact range of a wandering
/// NPC) or define arrival by something other than a point (far enough from a threat).
/// </summary>
internal interface IGoal
{
    /// <summary>Where to path to. May move (an NPC) — MoveRequest re-paths on drift.</summary>
    Vector3 Target { get; }
    /// <summary>Handed to Mnemosyne as the goal tolerance and to the follower as the stop
    /// range; 0 = exact.</summary>
    float PlannerTolerance { get; }
    bool IsSatisfied(Vector3 player);
    string Describe();
}

/// <summary>Within range of a fixed point (range 0: exactly there). The classic MoveTo/MoveCloseTo.</summary>
internal sealed class GoalNear(Vector3 dest, float range) : IGoal
{
    public Vector3 Target => dest;
    public float PlannerTolerance => range;
    public bool IsSatisfied(Vector3 player) => range > 0 && Vector3.Distance(player, dest) <= range;
    public string Describe() => range > 0 ? $"{dest:f1} within {range:0.#}y" : $"{dest:f1}";
}

/// <summary>Inside interact range of a live game object: base range plus its hitbox radius,
/// measured to its current position. The object may wander; the last known position stands
/// in if it despawns.</summary>
internal sealed class GoalInteract : IGoal
{
    private readonly Func<(Vector3 Position, float HitboxRadius)?> _resolve;
    private readonly float _baseRange;
    private Vector3 _lastKnown;
    private float _hitbox;

    private GoalInteract(Func<(Vector3 Position, float HitboxRadius)?> resolve, float baseRange, (Vector3 Position, float HitboxRadius) now)
    {
        _resolve = resolve;
        _baseRange = baseRange;
        (_lastKnown, _hitbox) = now;
    }

    /// <summary>Null when the object cannot be resolved right now — nothing to path to.</summary>
    public static GoalInteract? For(Func<(Vector3 Position, float HitboxRadius)?> resolve, float baseRange)
        => resolve() is { } now ? new GoalInteract(resolve, baseRange, now) : null;

    public Vector3 Target
    {
        get
        {
            Refresh();
            return _lastKnown;
        }
    }

    public float PlannerTolerance => _baseRange + _hitbox;

    public bool IsSatisfied(Vector3 player)
    {
        Refresh();
        return Vector3.Distance(player, _lastKnown) <= PlannerTolerance;
    }

    public string Describe() => $"interact range of {_lastKnown:f1} ({PlannerTolerance:0.#}y)";

    private void Refresh()
    {
        if (_resolve() is { } now)
            (_lastKnown, _hitbox) = now;
    }
}

/// <summary>At least a distance away from a point (a threat, a puddle). The target is a
/// reachable escape point MoveRequest resolves against the mesh before pathing.</summary>
internal sealed class GoalAway : IGoal
{
    public GoalAway(Vector3 from, float distance)
    {
        From = from;
        Distance = distance;
        Target = from;
    }

    public Vector3 From { get; }
    public float Distance { get; }
    public Vector3 Target { get; set; }
    public float PlannerTolerance => 0;
    public bool IsSatisfied(Vector3 player) => Vector3.Distance(player, From) >= Distance;
    public string Describe() => $"{Distance:0.#}y away from {From:f1}";
}
