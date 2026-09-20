using System;

namespace Ariadne.Mnemosyne;

/// <summary>
/// Shape checks for a `reachableCells` answer (spec: docs/mnemosyne-protocol.md). The three
/// arrays are parallel — one entry per *surface*, not per column — and `columns` indexes a
/// width×depth grid, so a server that sent mismatched lengths or a column outside the grid
/// would have consumers reading past their own array. Mnemosyne is a separate program with its
/// own versioning, and "never trust the server" is this client's standing rule: an unusable
/// grid is refused rather than passed on, and the caller reports `failed`.
/// </summary>
internal static class ReachableGrid
{
    public static bool TryValidate(int[] columns, float[] heights, int[] states, int width, int depth, out string whyNot)
    {
        if (columns.Length != heights.Length || columns.Length != states.Length)
        {
            whyNot = $"({columns.Length} columns, {heights.Length} heights, {states.Length} states — they are parallel arrays)";
            return false;
        }
        if (width <= 0 || depth <= 0)
        {
            whyNot = $"({columns.Length} surfaces on a {width}×{depth} grid)";
            return false;
        }

        // long: a hostile/absurd size must not overflow the bound it is checked against
        var cells = (long)width * depth;
        foreach (var column in columns)
        {
            if (column < 0 || column >= cells)
            {
                whyNot = $"column {column} is outside a {width}×{depth} grid";
                return false;
            }
        }

        whyNot = "";
        return true;
    }

    /// <summary>The wire carries the state as int (1 reachable · 2 cutOff); the consumer-facing
    /// tuple is byte[] per docs/consumer-ipc.md.</summary>
    public static byte[] ToStates(int[] states)
    {
        var result = new byte[states.Length];
        for (var i = 0; i < states.Length; i++)
            result[i] = (byte)Math.Clamp(states[i], 0, 255);
        return result;
    }
}
