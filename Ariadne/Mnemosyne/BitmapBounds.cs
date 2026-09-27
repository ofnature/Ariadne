using System.Numerics;

namespace Ariadne.Mnemosyne;

/// <summary>
/// The world-space region a rasterized bitmap covers — or an explicit "no answer".
///
/// <para>vnavmesh's <c>BuildBitmap*</c> return <c>(min, max)</c> synchronously from an in-process
/// rasterization. Ariadne proxies the same call to a server-side one behind a bounded wait, so
/// there are three ways to end up with nothing: the wait ran out, the server refused, or it
/// answered without reporting bounds on an unbounded request.</para>
///
/// <para>Those used to come back as <c>default</c> — <c>(0,0,0)</c> to <c>(0,0,0)</c> — which is a
/// plausible spot in most zones, cannot be told apart from a genuinely degenerate bitmap, and
/// described a file that was never written. <see cref="NoAnswer"/> says "I do not have one"
/// instead, and <see cref="HasAnswer"/> is how a caller checks: comparing against zero would be a
/// guess, since zero bounds are not impossible, only useless.</para>
/// </summary>
internal static class BitmapBounds
{
    /// <summary>No bounds to report: NaN on every component, so no arithmetic can quietly produce a
    /// region out of it and no comparison against a real one can succeed by accident.</summary>
    public static (Vector3 Min, Vector3 Max) NoAnswer => (new Vector3(float.NaN), new Vector3(float.NaN));

    /// <summary>True when these are real bounds rather than <see cref="NoAnswer"/>. Use this, not a
    /// comparison with zero.</summary>
    public static bool HasAnswer((Vector3 Min, Vector3 Max) bounds) => !float.IsNaN(bounds.Min.X);
}
