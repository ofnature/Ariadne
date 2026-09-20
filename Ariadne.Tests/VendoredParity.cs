using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ariadne.Tests;

/// <summary>
/// Skips a vendored-parity test when the reference clone is not on this machine:
/// external/ffxiv_navmesh is gitignored — a local behavioural reference that is deliberately
/// not part of the repo — so a fresh clone simply has nothing to compare against.
/// </summary>
public sealed class NeedsVendoredReferenceAttribute : FactAttribute
{
    public NeedsVendoredReferenceAttribute()
    {
        if (VendoredParity.Root == null)
            Skip = "external/ffxiv_navmesh is not present — vendored-parity checks skipped.";
    }
}

/// <summary>
/// Compares Ariadne's vendored copies against the reference clone (PLAN.md's "Key-parity
/// drift" risk: vnavmesh has changed its cache key between releases, and the only regression
/// test was a manual in-game comparison of a logged key against a meshcache filename).
///
/// The comparison is deliberately about <i>behaviour</i>: comments, whitespace, access
/// modifiers and the namespace differ by construction (the copies are internal, Ariadne's
/// header comments explain the provenance), so all of those are normalized away. What survives
/// is the code itself — expressions, signatures, constants — which must still be upstream's.
/// </summary>
public static class VendoredParity
{
    /// <summary>Repo root, or null when the reference clone is not checked out here.</summary>
    public static readonly string? Root = FindRoot();

    public static string Ref(string relative) => Path.Combine(Root!, relative);

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ariadne.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "external", "ffxiv_navmesh")))
                return dir.FullName;
        }
        return null;
    }

    // ---- normalization -------------------------------------------------------------------

    private static readonly string[] DroppedModifiers =
        ["public", "private", "protected", "internal", "readonly", "new", "async"];

    private static readonly string[] KeptModifiers =
        ["static", "unsafe", "const", "extern", "partial", "abstract", "sealed", "virtual", "override"];

    /// <summary>
    /// One comment-free, whitespace-collapsed, modifier-normalized line per source line, with
    /// `using`/`namespace` lines dropped (they are the one difference every copy has).
    /// Access modifiers are dropped and the remaining ones sorted, so the copies' `internal` and
    /// upstream's `public`, or `static unsafe` and `unsafe static`, compare equal.
    /// </summary>
    public static List<string> Normalize(string path)
    {
        var result = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = StripComment(raw.TrimStart('\uFEFF')).Trim();
            if (line.Length == 0 || line.StartsWith("namespace ", StringComparison.Ordinal)
                || line.StartsWith("using ", StringComparison.Ordinal))
                continue;
            result.Add(NormalizeModifiers(Regex.Replace(line, @"\s+", " ")));
        }
        return result;
    }

    /// <summary>Removes a trailing `//` comment while leaving `//` inside strings and char
    /// literals alone (signature strings and the `'/'`→`'_'` substitution both contain one).</summary>
    private static string StripComment(string line)
    {
        var inString = false;
        var inChar = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && (inString || inChar))
            {
                i++;
                continue;
            }
            if (!inChar && c == '"')
                inString = !inString;
            else if (!inString && c == '\'')
                inChar = !inChar;
            else if (!inString && !inChar && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                return line[..i];
        }
        return line;
    }

    private static string NormalizeModifiers(string line)
    {
        var tokens = line.Split(' ');
        var kept = new List<string>();
        var i = 0;
        while (i < tokens.Length && (DroppedModifiers.Contains(tokens[i]) || KeptModifiers.Contains(tokens[i])))
        {
            if (KeptModifiers.Contains(tokens[i]))
                kept.Add(tokens[i]);
            i++;
        }
        if (i == 0)
            return line; // no modifiers at the front: nothing to normalize
        kept.Sort(StringComparer.Ordinal);
        var rest = string.Join(' ', tokens[i..]);
        return kept.Count == 0 ? rest : string.Join(' ', kept) + " " + rest;
    }

    // ---- assertions ---------------------------------------------------------------------

    /// <summary>The copy's normalized body must equal the reference's, line for line.</summary>
    public static void AssertIdentical(string localRelative, string referenceRelative)
    {
        var local = Normalize(Ref(localRelative));
        var reference = Normalize(Ref(referenceRelative));

        var report = new StringBuilder();
        var shown = 0;
        for (var i = 0; i < Math.Min(local.Count, reference.Count) && shown < 8; i++)
        {
            if (local[i] == reference[i])
                continue;
            report.AppendLine($"  line {i + 1}:");
            report.AppendLine($"    ours:      {local[i]}");
            report.AppendLine($"    reference: {reference[i]}");
            shown++;
        }
        if (local.Count != reference.Count)
            report.AppendLine($"  {local.Count} lines here vs {reference.Count} in the reference "
                + "(comments, whitespace, access modifiers and the namespace are ignored)");

        Assert.True(report.Length == 0,
            $"{localRelative} has drifted from {referenceRelative}:\n{report}\n"
            + "It is a vendored copy: check the behaviour against the reference (in-game, for the "
            + "signature-scanned interop), then either re-vendor it or record the deliberate "
            + "deviation in its header comment.");
    }

    /// <summary>Equality with a message that says which side is which: a bare comparison of two
    /// short skeletons ("______" against "_____") reads as noise when it fires, and the person
    /// reading it is being asked to re-verify the cache key by hand.</summary>
    public static void AssertSame(string what, string referenceValue, string ours)
    {
        Assert.True(referenceValue == ours,
            $"{what} no longer matches the reference:\n"
            + $"    upstream: {referenceValue}\n"
            + $"    ours:     {ours}\n"
            + "Re-check the behaviour against the reference, then re-vendor the copy deliberately "
            + "(milestone 1's in-game key-vs-filename check is the behavioural test).");
    }

    /// <summary>The copy may be a trimmed subset of the reference (fewer helpers), but every
    /// line it does keep must still be the reference's line.</summary>
    public static void AssertSubsetOf(string localRelative, string referenceRelative, string why)
    {
        var local = Normalize(Ref(localRelative));
        var reference = Normalize(Ref(referenceRelative));

        var cursor = 0;
        var missing = new List<string>();
        foreach (var line in local)
        {
            var found = -1;
            for (var i = cursor; i < reference.Count; i++)
            {
                if (reference[i] != line)
                    continue;
                found = i;
                break;
            }
            if (found < 0)
                missing.Add(line); // keep looking from where we were: a later copy may match
            else
                cursor = found + 1;
        }

        Assert.True(missing.Count == 0,
            $"{localRelative} has {missing.Count} line(s) that are no longer in {referenceRelative}:\n"
            + string.Join("\n", missing.Take(12).Select(l => $"    {l}"))
            + $"\n{why}");
    }

    // ---- the cache key ------------------------------------------------------------------

    /// <summary>The first `return $"..."` inside a method, looked up by a declaration fragment
    /// (the reference is a whole plugin, so the search has to be anchored to the method).</summary>
    public static string ReturnOf(List<string> lines, string declarationFragment)
    {
        var at = lines.FindIndex(l => l.Contains(declarationFragment, StringComparison.Ordinal));
        Assert.True(at >= 0, $"the reference no longer declares '{declarationFragment}' — re-check the vendored logic by hand");
        if (at >= 0)
        {
            for (var i = at; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("return $\"", StringComparison.Ordinal))
                    return lines[i];
            }
            Assert.Fail($"'{declarationFragment}' no longer has a single-line interpolated return — re-check the vendored logic by hand");
        }
        return "";
    }

    /// <summary>An interpolated string split into the literal text around its placeholders and
    /// the placeholder expressions themselves. The expressions differ between the copies by
    /// construction — upstream inlines what Ariadne factored into helpers — but the literal text
    /// is what becomes a filename, and that must match exactly.</summary>
    public static (string Skeleton, List<string> Placeholders) Interpolation(string line)
    {
        var open = line.IndexOf("$\"", StringComparison.Ordinal);
        Assert.True(open >= 0, $"no interpolated string on: {line}");
        if (open < 0)
            return ("", []);

        var text = line[(open + 2)..line.LastIndexOf('"')];
        var skeleton = new StringBuilder();
        var placeholders = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (c == '{')
            {
                depth++;
                if (depth == 1)
                {
                    current.Clear();
                    continue;
                }
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    placeholders.Add(current.ToString());
                    continue;
                }
            }

            if (depth == 0)
                skeleton.Append(c);
            else
                current.Append(c);
        }
        return (skeleton.ToString(), placeholders);
    }

    /// <summary>The expression-bodied member's body, e.g. upstream's local `numbers` helper
    /// against Ariadne's `FormatNumbers`. The member may wrap across lines (the copies are
    /// reformatted), so the declaration is joined with what follows it until the body ends.</summary>
    public static string AfterArrow(List<string> lines, string declarationFragment)
    {
        var at = lines.FindIndex(l => l.Contains(declarationFragment, StringComparison.Ordinal));
        Assert.True(at >= 0, $"the reference no longer declares '{declarationFragment}' — re-check the vendored logic by hand");
        if (at < 0)
            return "";

        var joined = "";
        for (var i = at; i < lines.Count && i < at + 4; i++)
        {
            joined = joined.Length == 0 ? lines[i] : joined + " " + lines[i];
            var arrow = joined.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0)
                continue;
            var body = joined[(arrow + 2)..].Trim();
            if (body.EndsWith(";", StringComparison.Ordinal))
                return body;
        }
        Assert.Fail($"'{declarationFragment}' no longer reads as an expression-bodied member — re-check the vendored logic by hand");
        return "";
    }
}
