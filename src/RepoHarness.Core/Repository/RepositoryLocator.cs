using RepoHarness.Core.Git;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Repository;

/// <inheritdoc cref="IRepositoryLocator"/>
public sealed class RepositoryLocator(IGitClient gitClient) : IRepositoryLocator
{
    private readonly IGitClient _gitClient = gitClient;

    public async Task<HarnessLayout?> LocateAsync(
        string startDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var repositoryRoot = await _gitClient
            .GetRepositoryRootAsync(startDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (repositoryRoot is null)
        {
            return null;
        }

        // Asking git for the main worktree is what makes worktree handling correct
        // without parsing the ".git" file ourselves. A worktree created on Windows
        // records a drive-lettered gitdir path that a POSIX git cannot follow, and
        // hand-rolled parsing of it is a defect this avoids entirely.
        var main = await _gitClient
            .GetMainWorktreeAsync(startDirectory, cancellationToken)
            .ConfigureAwait(false);

        // There is deliberately no fallback to the tree itself. Inside a linked
        // worktree that guess is wrong, and every path derived from it moves with it:
        // worktrees would nest, and the run lock and ssh secrets would resolve inside
        // the worktree, where two runs of the same leg could no longer see each other.
        if (main is null)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git found a repository at '{repositoryRoot}' but did not list its main "
                + "worktree, so the main checkout cannot be located.");
        }

        if (main.IsBare)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{repositoryRoot}' is a worktree of the bare repository '{main.Path}'. "
                + "DssHarness keeps its state in the main checkout, and a bare repository has none.");
        }

        return new HarnessLayout(repositoryRoot, main.Path);
    }
}
