namespace RepoHarness.Core.Git;

/// <summary>Where a directory sits in its repository.</summary>
/// <param name="Prefix">
/// The directory's path below the root of its work tree, ending in a slash; empty at the root.
/// </param>
/// <param name="GitDirectory">
/// The git directory of that work tree, absolute, with symbolic links followed. A linked
/// worktree's is its own directory under the <c>worktrees</c> directory of the repository's
/// common git directory.
/// </param>
public sealed record GitLocation(string Prefix, string GitDirectory);
