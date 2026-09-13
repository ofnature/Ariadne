using Ariadne.Ipc;

namespace Ariadne.Tests;

// The gate policy: Dalamud IPC names are one global slot each (last registrar wins,
// unregister empties the slot). Decide() says whether to register ours, release ours, or
// leave the slot alone, given config and what was observed.
public class VnavCompatPolicyTests
{
    [Theory]
    // compat off: never claim; let go of anything still held
    [InlineData(false, false, false, false, CompatVerdict.None)]
    [InlineData(false, false, false, true, CompatVerdict.Release)]
    [InlineData(false, true, true, true, CompatVerdict.Release)]
    // claim-when-absent: vnavmesh absent -> ours; present -> its gates stay untouched
    [InlineData(true, false, false, false, CompatVerdict.Register)]
    [InlineData(true, false, false, true, CompatVerdict.None)]
    [InlineData(true, false, true, false, CompatVerdict.None)]
    // vnavmesh loaded and we still hold the slot (takeover just switched off): release
    [InlineData(true, false, true, true, CompatVerdict.Release)]
    // takeover: ours regardless of vnavmesh; re-register when it overwrote us
    [InlineData(true, true, true, false, CompatVerdict.Register)]
    [InlineData(true, true, true, true, CompatVerdict.None)]
    [InlineData(true, true, false, false, CompatVerdict.Register)]
    [InlineData(true, true, false, true, CompatVerdict.None)]
    public void Decide(bool enabled, bool takeover, bool vnavLoaded, bool owned, CompatVerdict expected)
    {
        Assert.Equal(expected, VnavCompatIpc.Decide(enabled, takeover, vnavLoaded, owned));
    }
}
