namespace RepoHarness.Tests;

/// <summary>Build records as 0.5.8 wrote them, which a build directory may still hold, and which are read as they were.</summary>
internal static class EarlierRecord
{
    /// <summary>
    /// The record 0.5.8 would have written for the build this version recorded as <paramref name="record"/>:
    /// its mark alone on the first line - <c>clock-stepped</c> where <paramref name="unordered"/>, <c>clean</c>
    /// otherwise - then the variant, then a fingerprint line for each input, with no reason, no dates and no
    /// newest file.
    /// </summary>
    /// <param name="record">A record this version wrote.</param>
    /// <param name="unordered">Whether 0.5.8 marked the build unordered.</param>
    public static string Written(string record, bool unordered)
    {
        ArgumentNullException.ThrowIfNull(record);

        var lines = record
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Where(line => !line.StartsWith("why ", StringComparison.Ordinal))
            .ToList();

        return string.Join(
            '\n',
            [unordered ? "clock-stepped" : "clean", lines[0], .. lines.Skip(1).Where(line => line.StartsWith("in ", StringComparison.Ordinal))]) + "\n";
    }
}
