using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Anchors;

/// <summary>Finds the registry files a command works on.</summary>
public interface IAnchorRegistryLocator
{
    /// <summary>Locates both registries for the tree <paramref name="context"/> describes.</summary>
    Task<AnchorRegistries> LocateAsync(HarnessContext context, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAnchorRegistryLocator"/>
/// <remarks>
/// A registry git tracks is part of the branch, so it is read and changed in the tree the command
/// runs in: a change made in a worktree travels with that worktree's branch. A registry git ignores
/// belongs to no branch and a worktree has no copy of it, so it is found where ignored harness state
/// always lives, in the main checkout.
/// </remarks>
public sealed class AnchorRegistryLocator(IGitClient gitClient) : IAnchorRegistryLocator
{
    private readonly IGitClient _gitClient = gitClient;

    public async Task<AnchorRegistries> LocateAsync(HarnessContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = context.Config.Anchors;

        var pending = await LocateAsync(context.Layout, AnchorRegistryKind.Pending, settings.PendingAnchorsPath, cancellationToken)
            .ConfigureAwait(false);
        var done = await LocateAsync(context.Layout, AnchorRegistryKind.Done, settings.DoneAnchorsPath, cancellationToken)
            .ConfigureAwait(false);

        return new AnchorRegistries(pending, done);
    }

    /// <summary>
    /// The configured path as git and reports want it: forward slashes, and no empty or <c>.</c> segments.
    /// Every use of a configured registry path goes through this, so two spellings of one path are always
    /// one registry.
    /// </summary>
    public static string Normalize(string configuredPath)
    {
        ArgumentNullException.ThrowIfNull(configuredPath);

        return string.Join('/', configuredPath
            .Split('/', '\\')
            .Where(segment => segment.Length > 0 && segment != "."));
    }

    private async Task<AnchorRegistry> LocateAsync(
        HarnessLayout layout,
        AnchorRegistryKind kind,
        string configuredPath,
        CancellationToken cancellationToken)
    {
        var relative = Normalize(configuredPath);

        var ignored = await _gitClient
            .IsIgnoredAsync(layout.RepositoryRoot, relative, cancellationToken)
            .ConfigureAwait(false);

        var root = ignored ? layout.MainCheckoutRoot : layout.RepositoryRoot;

        return new AnchorRegistry(kind, relative, Path.GetFullPath(Path.Combine(root, relative)), ignored);
    }
}
