namespace RepoHarness.Core.Git;

/// <summary>
/// Result of <c>DssHarness verify-git</c>, returned directly as the process exit code.
/// </summary>
/// <remarks>
/// The two failure values are deliberately distinct: "git is not installed" is a
/// machine setup problem, "this is not a repository" is a location problem, and
/// they call for different remedies.
/// </remarks>
public enum VerifyGitStatus
{
    /// <summary>Git is installed and the directory is inside a repository.</summary>
    Success = 0,

    /// <summary>Git is not installed or not on PATH.</summary>
    GitNotInstalled = 1,

    /// <summary>Git works, but the directory is not inside a repository.</summary>
    NotAGitRepository = 2,
}
