namespace RepoHarness.Core.Git;

/// <summary>One entry of <c>git worktree list</c>.</summary>
/// <param name="Path">
/// Absolute path of the worktree's root, as git resolves it: symbolic links followed.
/// </param>
/// <param name="Commit">Commit the worktree is checked out at, or <see langword="null"/> when git reports none.</param>
/// <param name="Branch">Branch name, or <see langword="null"/> when detached.</param>
/// <param name="IsMain">Whether this is the main worktree rather than a linked one.</param>
/// <param name="IsBare">Whether this entry is a bare repository, which has no checkout at all.</param>
public sealed record GitWorktree(string Path, string? Commit, string? Branch, bool IsMain, bool IsBare);
