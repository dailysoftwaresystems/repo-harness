namespace RepoHarness.Core.Repository;

/// <summary>
/// Maintains the harness's own block inside a repository's <c>.gitignore</c>.
/// </summary>
public interface IGitIgnoreManager
{
    /// <summary>
    /// Returns <paramref name="existingContent"/> with the managed block set to
    /// <paramref name="lines"/>, replacing a previous block when one is present and
    /// appending otherwise. A pure transformation: nothing outside the markers is
    /// touched, so hand written rules survive every run.
    /// </summary>
    string ApplyManagedBlock(string? existingContent, IReadOnlyList<string> lines);

    /// <summary>
    /// Applies the managed block to the file at <paramref name="path"/>.
    /// Returns whether the file's content changed.
    /// </summary>
    bool Update(string path, IReadOnlyList<string> lines);

    /// <summary>
    /// The hand-written rules in <paramref name="content"/> that rule on a path one of
    /// <paramref name="lines"/> also rules on, in the order they appear.
    /// </summary>
    /// <param name="content">The whole <c>.gitignore</c>, managed block included.</param>
    /// <param name="lines">The rules the managed block holds.</param>
    /// <remarks>
    /// A path is compared exactly, after dropping what only changes a rule's shape: a leading
    /// <c>!</c>, one anchoring <c>/</c>, a trailing <c>/*</c> and a trailing <c>/</c>. Nothing is
    /// matched as a glob, so a hand-written rule that reaches a managed path only through a
    /// wildcard, such as <c>**/worktrees/</c>, is not reported.
    /// </remarks>
    IReadOnlyList<GitIgnoreOverlap> FindOverlaps(string? content, IReadOnlyList<string> lines);
}

/// <summary>A hand-written ignore rule that states again a path the managed block rules on.</summary>
/// <param name="LineNumber">Where the rule is, counting from one.</param>
/// <param name="Rule">The rule as it is written.</param>
/// <param name="Path">The path both rules name, without the parts that only change a rule's shape.</param>
/// <param name="ReIncludes">Whether the hand-written rule re-includes the path rather than ignoring it.</param>
/// <param name="Contradicts">
/// Whether the two rules point opposite ways — one ignoring the path, the other re-including it.
/// Kept apart from a repetition because it is the overlap that can undo the managed block:
/// whichever of two contradicting rules comes later in the file wins, so a hand-written
/// re-include after the block can put a secret back in reach of <c>git add</c>.
/// </param>
public sealed record GitIgnoreOverlap(int LineNumber, string Rule, string Path, bool ReIncludes, bool Contradicts);
