using Ariadne.Seeding;
using Ariadne.Zone;
using System;
using System.Text.RegularExpressions;

namespace Ariadne.Tests;

// The vendored copies are the part of Ariadne that is not Ariadne: they are vnavmesh's logic,
// and vnavmesh moves. These tests fail the moment the copies no longer match the reference
// clone, which is the moment someone has to decide between re-vendoring and recording a
// deliberate deviation. They skip when the clone is absent (it is gitignored).
public class VendoredParityTests
{
    [NeedsVendoredReference]
    public void VendoredCopies_MatchTheReference()
    {
        // signature-scanned interop (re-check on every game patch) — the header comments say
        // "verbatim apart from the Service class", and the Service members share their names
        VendoredParity.AssertIdentical("Ariadne/Movement/OverrideMovement.cs", "external/ffxiv_navmesh/vnavmesh/Movement/OverrideMovement.cs");
        VendoredParity.AssertIdentical("Ariadne/Movement/OverrideCamera.cs", "external/ffxiv_navmesh/vnavmesh/Movement/OverrideCamera.cs");
        VendoredParity.AssertIdentical("Ariadne/Movement/OverrideAfk.cs", "external/ffxiv_navmesh/vnavmesh/Movement/OverrideAfk.cs");

        // the rest of the vendored logic
        VendoredParity.AssertIdentical("Ariadne/Movement/Angle.cs", "external/ffxiv_navmesh/vnavmesh/Angle.cs");
        VendoredParity.AssertIdentical("Ariadne/Zone/SceneDefinition.cs", "external/ffxiv_navmesh/vnavmesh/SceneDefinition.cs");
    }

    [NeedsVendoredReference]
    public void TrimmedLayoutUtils_IsStillAVerbatimSubset()
    {
        VendoredParity.AssertSubsetOf("Ariadne/Zone/LayoutUtils.cs", "external/ffxiv_navmesh/vnavmesh/LayoutUtils.cs",
            "LayoutUtils is deliberately trimmed (it keeps fewer helpers than upstream), but every helper it does "
            + "keep must still be upstream's line, because the cache key is built out of these.");
    }

    [NeedsVendoredReference]
    public void LayoutKey_FormatStillMatchesUpstream()
    {
        var reference = VendoredParity.Normalize(VendoredParity.Ref("external/ffxiv_navmesh/vnavmesh/NavmeshManager.cs"));
        var ours = VendoredParity.Normalize(VendoredParity.Ref("Ariadne/Zone/ZoneKey.cs"));

        var (refSkeleton, refFields) = VendoredParity.Interpolation(VendoredParity.ReturnOf(reference, "string GetCurrentKey("));
        var (ourSkeleton, ourFields) = VendoredParity.Interpolation(VendoredParity.ReturnOf(ours, "string CurrentKey("));

        // the file's header: vnavmesh logs this key on every zone change and it must read the same
        Assert.Equal("//////", ourSkeleton); // {Bg}//{filter:X}//{festivals}//{sgs} — three "//" separators
        VendoredParity.AssertSame("The layout key skeleton", refSkeleton, ourSkeleton);
        Assert.Equal(refFields, ourFields); // same helpers, same order — the expressions are copied too
    }

    [NeedsVendoredReference]
    public void CacheKey_FormatStillMatchesUpstream()
    {
        var reference = VendoredParity.Normalize(VendoredParity.Ref("external/ffxiv_navmesh/vnavmesh/NavmeshManager.cs"));
        var ours = VendoredParity.Normalize(VendoredParity.Ref("Ariadne/Zone/ZoneKey.cs"));

        var (refSkeleton, refFields) = VendoredParity.Interpolation(VendoredParity.ReturnOf(reference, "string GetCacheKey("));
        var (ourSkeleton, ourFields) = VendoredParity.Interpolation(VendoredParity.ReturnOf(ours, "string CurrentCacheKey("));

        // the meshcache filename that milestone 1 verified in-game, byte for byte:
        // {bg with / → _}__{filter:X}__{festivals hex}__{sgs hex}
        Assert.Equal("______", ourSkeleton);
        VendoredParity.AssertSame("The meshcache key skeleton", refSkeleton, ourSkeleton);

        // the filter key stays uppercase hex, and every numeric field is joined as hex — the
        // expression upstream keeps in a local `numbers<T>` helper lives in FormatNumbers here
        Assert.Contains(refFields, f => f.Contains("filterKey:X"));
        Assert.Contains(ourFields, f => f.Contains("filterKey:X"));
        Assert.Contains(refFields, f => f == "terrRow?.Bg.ToString().Replace('/', '_')");
        Assert.Contains(ourFields, f => f == "terrRow?.Bg.ToString().Replace('/', '_')");
        Assert.Equal(VendoredParity.AfterArrow(reference, "string numbers<"), VendoredParity.AfterArrow(ours, "string FormatNumbers("));
    }

    [NeedsVendoredReference]
    public void NavmeshFileFormat_StillMatchesUpstream()
    {
        // a serializer bump upstream means every mesh Ariadne hands around is rejected on load:
        // seeding would quietly stop saving time, which is exactly the failure the header gate
        // exists to make loud on the client side — but only if the constant tracks upstream
        var reference = string.Join("\n", VendoredParity.Normalize(VendoredParity.Ref("external/ffxiv_navmesh/vnavmesh/Navmesh.cs")));

        var magic = Regex.Match(reference, @"\buint Magic = (0x[0-9A-Fa-f]+);");
        var version = Regex.Match(reference, @"\buint Version = (\d+);");
        Assert.True(magic.Success, "Navmesh.Magic no longer parses out of the reference — re-check by hand");
        Assert.True(version.Success, "Navmesh.Version no longer parses out of the reference — re-check by hand");

        Assert.True(NavmeshHeader.ExpectedMagic == Convert.ToUInt32(magic.Groups[1].Value, 16),
            $"the mesh magic moved upstream: reference {magic.Groups[1].Value}, "
            + $"NavmeshHeader.ExpectedMagic is 0x{NavmeshHeader.ExpectedMagic:X}");
        Assert.True(NavmeshHeader.CurrentVersion == int.Parse(version.Groups[1].Value),
            $"the serializer version moved upstream: reference v{version.Groups[1].Value}, "
            + $"NavmeshHeader.CurrentVersion is v{NavmeshHeader.CurrentVersion} — every mesh Ariadne "
            + "hands vnavmesh would be rejected on load, and seeding would quietly stop saving time");
    }
}
