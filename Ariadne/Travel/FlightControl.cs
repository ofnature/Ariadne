using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;

namespace Ariadne.Travel;

/// <summary>
/// The game's side of flying: whether flight is unlocked where the character stands, and the
/// mount going out and away. Main thread only.
///
/// <para>"Unlocked" is the game's own answer, <c>IsAetherCurrentZoneComplete</c> for the
/// territory's current set — the check Odysseus and Questionable fly on. It covers the A Realm
/// Reborn zones as well: they have no currents to collect, their sets complete with the main
/// story, and the game reports them through the same call. A territory with no set (cities,
/// interiors, duties) cannot be flown in.</para>
/// </summary>
internal sealed unsafe class FlightControl
{
    private const uint MountRoulette = 9;  // General Action
    private const uint Dismount = 23;      // General Action

    public bool IsMounted => Service.Condition[ConditionFlag.Mounted];
    public bool IsFlying => Service.Condition[ConditionFlag.InFlight];

    /// <summary>Swimming or diving: the character is at the water's height, not the ground's.</summary>
    public bool InWater => Service.Condition[ConditionFlag.Swimming] || Service.Condition[ConditionFlag.Diving];

    /// <summary>Flight is unlocked in this zone and nothing about the character forbids it
    /// right now.</summary>
    public bool CanFlyHere()
    {
        try
        {
            if (Service.Condition[ConditionFlag.InCombat] || Service.Condition[ConditionFlag.BoundByDuty]
                || Service.Condition[ConditionFlag.RidingPillion] || Service.Condition[ConditionFlag.BetweenAreas])
                return false;
            var territory = Service.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Service.ClientState.TerritoryType);
            if (territory is not { } t)
                return false;
            var set = t.AetherCurrentCompFlgSet.RowId;
            if (set == 0)
                return false;
            var state = PlayerState.Instance();
            return state != null && state->IsAetherCurrentZoneComplete(set);
        }
        catch (Exception ex)
        {
            Service.Log.Warning($"[Flight] could not tell whether flight is unlocked here: {ex.Message}");
            return false;
        }
    }

    /// <summary>Ask for the mount. True when it is out, on its way out, or was just asked
    /// for; false when the game will not take the request right now.</summary>
    public bool TryMount()
    {
        if (IsMounted || MountingUp)
            return true;
        var actions = ActionManager.Instance();
        if (actions == null || actions->GetActionStatus(ActionType.GeneralAction, MountRoulette) != 0)
            return false;
        return actions->UseAction(ActionType.GeneralAction, MountRoulette);
    }

    /// <summary>Put the mount away. True when it is away or was just asked to be.</summary>
    public bool TryDismount()
    {
        if (!IsMounted)
            return true;
        var actions = ActionManager.Instance();
        if (actions == null || actions->GetActionStatus(ActionType.GeneralAction, Dismount) != 0)
            return false;
        return actions->UseAction(ActionType.GeneralAction, Dismount);
    }

    private static bool MountingUp => Service.Condition[ConditionFlag.Casting]
        || Service.Condition[ConditionFlag.Mounting] || Service.Condition[ConditionFlag.Mounting71];
}
