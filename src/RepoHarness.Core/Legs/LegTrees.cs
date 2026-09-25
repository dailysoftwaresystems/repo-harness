using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Legs;

/// <summary>The tree a leg acts on here, and the tree its work happens in on the machine that runs it.</summary>
/// <remarks>
/// One answer for placing a leg and for asking a host about it beforehand, so the build directory a host is
/// asked about is the one the leg then builds in.
/// </remarks>
public static class LegTrees
{
    /// <summary>
    /// The tree <paramref name="leg"/> acts on here: the worktree it names, or the tree the command was typed in.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="here">The host this machine is to the machine that sent the leg here, or <see langword="null"/>.</param>
    /// <remarks>
    /// A leg naming a worktree acts on that tree; every path below derives from it, which is what keeps a
    /// worktree's build output out of the main checkout's build directory. On a host a leg was sent to, its
    /// tree is the copy it was sent to, which is that worktree's own: the worktree it names is on the machine
    /// that sent it, and nothing in the copy is at that path.
    /// </remarks>
    public static string Here(HarnessContext context, LegConfig leg, HostId? here)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(leg);

        return here is null && leg.Worktree is { Length: > 0 } worktree
            ? context.Layout.WorktreePathUnder(context.Config.Worktrees.Root, worktree)
            : context.Layout.RepositoryRoot;
    }

    /// <summary>
    /// Where the work on <paramref name="treeRoot"/> happens on <paramref name="host"/>: the tree itself on this
    /// machine, and that machine's copy of it - the main checkout's, or a worktree's own beside it - on any other.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="host">The machine the work runs on.</param>
    /// <param name="treeRoot">The tree here.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <exception cref="HarnessException">The host declares nowhere to keep a copy.</exception>
    public static string On(HarnessContext context, HostId host, string treeRoot, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);

        return host.Kind == HostKind.Local
            ? treeRoot
            : HostCopies.Of(context.Config, host, context.Layout, treeRoot, comparison);
    }
}
