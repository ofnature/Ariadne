using Dalamud.Plugin;
using System;

namespace Ariadne.Movement;

// Publishes "Ariadne is driving the character" through Dalamud shared data — the channel
// movement-adjacent plugins (BossMod) poll to decide whether to yield. Not IPC: shared-data
// tags aren't exclusively owned, so we can publish under vnavmesh's tag even while vnavmesh
// is loaded (whichever plugin last wrote wins, which is the correct semantics for "is
// anyone driving?"). Entries must be reference types, hence bool[1] — same as vnavmesh.
internal sealed class PathIsRunningSignal : IDisposable
{
    public const string AriadneTag = "ariadne.PathIsRunning";
    public const string VnavTag = "vnav.PathIsRunning";

    private readonly IDalamudPluginInterface _pi;
    private readonly Func<bool> _mirrorVnav;
    private readonly bool[] _ariadne;
    private bool[]? _vnav;
    private bool _last;

    public PathIsRunningSignal(IDalamudPluginInterface pi, Func<bool> mirrorVnav)
    {
        _pi = pi;
        _mirrorVnav = mirrorVnav;
        _ariadne = _pi.GetOrCreateData<bool[]>(AriadneTag, () => [false]);
    }

    public void Set(bool running)
    {
        _ariadne[0] = running;

        if (_mirrorVnav())
        {
            _vnav ??= _pi.GetOrCreateData<bool[]>(VnavTag, () => [false]);
            _vnav[0] = running;
        }
        else if (_vnav != null && _last)
        {
            _vnav[0] = false; // toggle turned off mid-path: don't leave a stale true behind
        }
        _last = running;
    }

    public void Dispose()
    {
        _ariadne[0] = false;
        if (_vnav != null)
            _vnav[0] = false;
        _pi.RelinquishData(AriadneTag);
        if (_vnav != null)
            _pi.RelinquishData(VnavTag);
    }
}
