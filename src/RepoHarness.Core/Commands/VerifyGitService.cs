using RepoHarness.Core.Git;

namespace RepoHarness.Core.Commands;

/// <summary>
/// Answers whether this machine and this directory can be worked with at all:
/// git installed, and a repository present.
/// </summary>
public sealed class VerifyGitService(IGitClient gitClient)
{
    private readonly IGitClient _gitClient = gitClient;

    /// <summary>
    /// Checks <paramref name="directory"/>. The two failure values are distinct
    /// because their remedies are: install git, versus go somewhere else.
    /// </summary>
    public async Task<VerifyGitStatus> VerifyAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!_gitClient.IsInstalled())
        {
            return VerifyGitStatus.GitNotInstalled;
        }

        var isRepository = await _gitClient
            .IsRepositoryAsync(directory, cancellationToken)
            .ConfigureAwait(false);

        return isRepository ? VerifyGitStatus.Success : VerifyGitStatus.NotAGitRepository;
    }
}
