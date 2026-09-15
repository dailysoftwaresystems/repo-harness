using System.Diagnostics.CodeAnalysis;

namespace RepoHarness.Core.Anchors;

/// <summary>
/// An anchor's priority band, from <c>P0</c>, the most urgent, to <c>P5</c>. A declaration made by
/// whoever writes the row: once set it survives every later edit that does not change it.
/// </summary>
public static class AnchorPriority
{
    /// <summary>Every band, most urgent first.</summary>
    public static IReadOnlyList<string> Bands { get; } = ["P0", "P1", "P2", "P3", "P4", "P5"];

    /// <summary>Reads a band given in either case, such as <c>p1</c>.</summary>
    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? band)
    {
        var candidate = (value ?? string.Empty).Trim().ToUpperInvariant();
        band = Bands.Contains(candidate, StringComparer.Ordinal) ? candidate : null;
        return band is not null;
    }

    /// <summary>Whether a Priority cell is exactly one of the bands.</summary>
    public static bool IsBand(string cell) => Bands.Contains(cell.Trim(), StringComparer.Ordinal);
}
