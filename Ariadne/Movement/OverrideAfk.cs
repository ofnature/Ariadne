using FFXIVClientStructs.FFXIV.Client.UI;

namespace Ariadne.Movement;

// Vendored from vnavmesh (external/ffxiv_navmesh/vnavmesh/Movement/OverrideAfk.cs), verbatim.
internal unsafe static class OverrideAFK
{
    public static void ResetTimers()
    {
        var module = UIModule.Instance()->GetInputTimerModule();
        module->AfkTimer = 0;
        module->ContentInputTimer = 0;
        module->InputTimer = 0;
    }
}
