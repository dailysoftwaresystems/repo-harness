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
}
