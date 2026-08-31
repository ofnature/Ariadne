using System.Numerics;

namespace Ariadne.Zone;

// Vendored from vnavmesh's MapUtils.GetFlagPosition — the map flag lives only in the game
// process (AgentMap), which is why FlagToPoint is Ariadne's to serve, not Mnemosyne's.
// Must be called on the framework thread (IPC calls arrive there).
internal static unsafe class MapFlag
{
    public static Vector2? GetFlagPosition()
    {
        var map = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
        if (map == null || map->FlagMarkerCount == 0)
            return null;
        var marker = map->FlagMapMarkers[0];
        return new(marker.XFloat, marker.YFloat);
    }
}
