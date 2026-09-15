namespace RepoHarness.Core.Git;

/// <summary>One entry of the index, as <c>git ls-files --stage -v</c> reports it.</summary>
/// <param name="Tag">
/// git's tag for the entry: lowercase when the file is marked assume-unchanged, S or s when it is
/// marked skip-worktree.
/// </param>
/// <param name="Mode">The octal mode, such as 100644 for a file or 160000 for a submodule.</param>
/// <param name="ObjectId">The object the index holds for the path.</param>
/// <param name="Stage">0 for a merged entry; 1 to 3 for the sides of an unresolved conflict.</param>
/// <param name="Path">
/// The path, relative to the directory git ran in; git lists only the entries below that directory.
/// </param>
public sealed record GitIndexEntry(char Tag, string Mode, string ObjectId, int Stage, string Path)
{
    /// <summary>Whether status skips the file, because it is marked assume-unchanged.</summary>
    public bool IsAssumedUnchanged => char.IsLower(Tag);

    /// <summary>Whether status skips the file, because it is marked skip-worktree.</summary>
    public bool IsSkipWorktree => Tag is 'S' or 's';

    /// <summary>Whether the entry records a submodule's commit rather than a file.</summary>
    public bool IsSubmodule => Mode == "160000";

    /// <summary>Whether the entry is an ordinary file, executable or not.</summary>
    public bool IsRegularFile => Mode is "100644" or "100755";
}
