namespace RepoHarness.Core.FileSystem;

/// <summary>Decides whether one path lies beneath another.</summary>
public static class PathContainment
{
    /// <summary>
    /// Whether <paramref name="candidate"/> lies strictly beneath <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Both paths are fully resolved first, so <c>..</c> segments cannot escape, and a
    /// separator is required at the boundary: a bare prefix test would also accept a
    /// sibling whose name merely starts with the same characters. A root is not beneath
    /// itself. This exists to guard a recursive delete, so every doubtful case answers no.
    /// </remarks>
    /// <param name="root">Directory the candidate must be inside.</param>
    /// <param name="candidate">Path being checked.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    public static bool IsStrictlyInside(string root, string candidate, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var resolvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var resolvedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        if (resolvedCandidate.Length <= resolvedRoot.Length
            || !resolvedCandidate.StartsWith(resolvedRoot, comparison))
        {
            return false;
        }

        // A filesystem root such as "C:\" or "/" keeps its separator after trimming, so
        // there the boundary is already the last character of the root itself.
        return IsSeparator(resolvedRoot[^1]) || IsSeparator(resolvedCandidate[resolvedRoot.Length]);
    }

    private static bool IsSeparator(char character)
        => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;
}
