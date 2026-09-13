using System.Collections.Generic;
using System.Numerics;

namespace Ariadne.Travel;

/// <summary>
/// The local teleport rule, until Mnemosyne's planner emits teleport legs itself: compare
/// the ETA of travelling directly against teleporting to each attuned aetheryte in the
/// zone and travelling from there. Pure — the tests spell it out.
/// </summary>
internal static class TeleportPlanner
{
    /// <summary>Mnemosyne's calibrated speeds (a 340 y flight estimated at 17 s flew in 17 s).</summary>
    public const float WalkSpeed = 6f;
    public const float FlySpeed = 20f;

    public sealed record Choice(uint AetheryteId, Vector3 Position, float DirectSeconds, float ViaSeconds);

    /// <param name="teleportCostSeconds">Cast + confirmation + loading screen.</param>
    /// <param name="minSavingSeconds">Only teleport when it wins by at least this much — a
    /// loading screen for a two-second gain is not a win.</param>
    public static Choice? Choose(Vector3 player, Vector3 goal, bool fly,
        IEnumerable<(uint Id, Vector3 Position)> attunedInZone, float teleportCostSeconds, float minSavingSeconds)
    {
        var speed = fly ? FlySpeed : WalkSpeed;
        var direct = Horizontal(player, goal) / speed;

        Choice? best = null;
        foreach (var (id, pos) in attunedInZone)
        {
            var via = teleportCostSeconds + Horizontal(pos, goal) / speed;
            if (best == null || via < best.ViaSeconds)
                best = new Choice(id, pos, direct, via);
        }

        return best != null && direct - best.ViaSeconds >= minSavingSeconds ? best : null;
    }

    /// <summary>XZ distance: marker-placed aetherytes have no height, and travel time is
    /// about ground covered anyway.</summary>
    public static float Horizontal(Vector3 a, Vector3 b)
    {
        var d = a - b;
        d.Y = 0;
        return d.Length();
    }
}
