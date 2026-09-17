namespace RepoHarness.Core.Repository;

/// <summary>
/// How a configured list of paths names what it covers, and the one place that decides whether a
/// path is covered.
/// </summary>
/// <remarks>
/// One matcher for every list of this shape: <c>sync.neverTransfer</c>, <c>sync.exclude</c>,
/// <c>lineEndings.exclude</c> and the roots an anchor cites. The question is asked on both sides of
/// a sync as well — here to build the plan, and on the host where the walk is told what not to list
/// — and two implementations of it are how a file ends up transferred by one side and protected by
/// the other, or covered by one setting and not by another that is written the same way.
/// <para>
/// An entry is rooted unless it says otherwise. <c>build</c> is the <c>build</c> beside the
/// repository and nothing else, which is what it has always meant and what a configuration written
/// before this still means. The alternative — a bare name covering that name at any depth, as
/// <c>.gitignore</c> reads it — would quietly stop transferring every nested directory of that name
/// to every host, and a build that needs <c>src/docs</c> would start failing on a host for a reason
/// nothing in the file changed to cause.
/// </para>
/// <para>
/// A name at any depth is spelled <c>**/name</c>, which says so. That is what a cache directory
/// needs: <c>__pycache__</c>, <c>node_modules</c> and <c>.venv</c> appear wherever their language
/// put them, and a list that can only name them one path at a time is a list nobody can keep
/// correct.
/// </para>
/// </remarks>
public static class PathPatterns
{
    /// <summary>What an entry starts with to mean "this name, at any depth".</summary>
    public const string AnyDepth = "**/";

    /// <summary>
    /// Puts a path in the one form both sides of a sync agree on: forward separators, no leading or
    /// trailing separator, no <c>./</c> prefix. A Windows source and a Linux copy otherwise share no
    /// spelling and every comparison misses.
    /// </summary>
    /// <param name="path">The path or pattern as written.</param>
    public static string Normalize(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');

        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> is covered by any of <paramref name="patterns"/>,
    /// which includes everything beneath a covered directory.
    /// </summary>
    /// <param name="patterns">The entries, as written.</param>
    /// <param name="relativePath">A path relative to the tree root, spelled either way.</param>
    public static bool Matches(IReadOnlyList<string> patterns, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var candidate = Normalize(relativePath);

        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (var written in patterns)
        {
            var pattern = Normalize(written);

            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern.StartsWith(AnyDepth, StringComparison.Ordinal))
            {
                if (CoversAnyDepth(pattern[AnyDepth.Length..], candidate))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(candidate, pattern, StringComparison.Ordinal)
                || candidate.StartsWith(pattern + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Why <paramref name="pattern"/> cannot be used, or <see langword="null"/> when it can.
    /// </summary>
    /// <param name="pattern">The entry, as written.</param>
    /// <remarks>
    /// Only the leading form is supported, and anything else holding <c>*</c> is refused rather
    /// than matched literally. A pattern that looks like a glob and is compared as text matches
    /// nothing, so a reader who writes <c>src/**/cache</c> gets a message naming the line instead of
    /// a list that silently protects nothing.
    /// </remarks>
    public static string? Problem(string? pattern)
    {
        var normalized = Normalize(pattern);

        if (normalized.Length == 0)
        {
            return "is empty";
        }

        var rest = normalized.StartsWith(AnyDepth, StringComparison.Ordinal)
            ? normalized[AnyDepth.Length..]
            : normalized;

        if (rest.Length == 0)
        {
            return $"is '{AnyDepth}' with no name after it; write the name it should match at any depth";
        }

        return rest.Contains('*', StringComparison.Ordinal)
            ? $"holds '*', which is only understood as a leading '{AnyDepth}' meaning that name at any depth"
            : null;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="name"/> at any depth, or anything
    /// beneath one.
    /// </summary>
    /// <param name="name">The name the pattern gave, which may itself hold separators.</param>
    /// <param name="candidate">The normalised path being tested.</param>
    private static bool CoversAnyDepth(string name, string candidate)
    {
        if (string.Equals(candidate, name, StringComparison.Ordinal)
            || candidate.StartsWith(name + "/", StringComparison.Ordinal))
        {
            return true;
        }

        // Matched on whole segments. Without that, '**/cache' would cover 'src/mycache', which is a
        // different directory with a similar name, and the reader would never see it go.
        var needle = "/" + name;
        var at = candidate.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            var after = at + needle.Length;

            if (after == candidate.Length || candidate[after] == '/')
            {
                return true;
            }

            at = candidate.IndexOf(needle, at + 1, StringComparison.Ordinal);
        }

        return false;
    }
}
