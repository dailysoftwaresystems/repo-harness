using RepoHarness.Core.Repository;
using RepoHarness.Core.Runners;

namespace RepoHarness.Core.Sync;

/// <summary>
/// What of the harness's own directory an ordinary sync carries: its runner actions, the way the
/// rest of the tree is carried, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The actions are part of the tree. A leg placed on a host runs there, and a runner's action is
/// read from that host's copy, so an action that never crosses is a runner no leg on another host
/// can run - which is how it was: the whole of this directory was withheld, and a host's copy held
/// no action file at all. They cross by the rule every other file follows, not by what git has
/// indexed: not ignored crosses, ignored never does. A new action a lane has not committed yet is
/// exactly the one it needs to try on a remote leg, and a worktree's own configuration naming it
/// must not arrive on a host that lacks it.
/// </para>
/// <para>
/// Everything else here stays on its machine, and this decides it in code rather than by
/// configuration or by git's ignore rules: connection data, credentials, runner values, locks and
/// run state are each local to a machine by design, and a .gitignore that has drifted must not be
/// able to send them anywhere. The same holds for each action's own <c>build</c> and
/// <c>artifacts</c>, at any depth: those are the run state of whichever machine made them, carried
/// between hosts only by name, with <c>sync --artifact</c>. <c>config.json</c> crosses too, by a step
/// of its own that writes it last.
/// </para>
/// <para>
/// One rule for both sides of a sync. The side that sends and the side that is written read their
/// trees through it, so a file one side would carry is a file the other side can see and, when the
/// tree no longer has it, remove: a stale action left on a host would otherwise sit above a new
/// grouped one and be refused there as an action inside an action.
/// </para>
/// </remarks>
public static class HarnessDirectorySync
{
    private static readonly string Harness = HarnessLayout.DirectoryName;

    private static readonly string Runner = $"{Harness}/{HarnessLayout.RunnerDirectoryName}";

    private static readonly string Actions = $"{Runner}/{HarnessLayout.RunnerActionsDirectoryName}";

    /// <summary>
    /// Whether <paramref name="relativePath"/> is inside the harness's own directory and is never
    /// carried by an ordinary sync, nor deleted by one.
    /// </summary>
    /// <param name="relativePath">A path relative to the tree root, with forward separators.</param>
    /// <remarks>
    /// False for a path outside that directory, which the ordinary rules decide, and false for the
    /// directories a walk has to open on its way to the actions and for everything under the
    /// actions that is not an action's own <c>build</c> or <c>artifacts</c>: those are decided by
    /// the ordinary rules too, exactly as the rest of the tree is.
    /// </remarks>
    public static bool Withholds(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (!IsAt(relativePath, Harness))
        {
            return false;
        }

        // The way down to the actions has to be opened, or nothing under it is ever seen.
        if (relativePath == Harness || relativePath == Runner || relativePath == Actions)
        {
            return false;
        }

        if (!relativePath.StartsWith(Actions + "/", StringComparison.Ordinal))
        {
            return true;
        }

        return relativePath[(Actions.Length + 1)..].Split('/').Any(ActionPath.Reserved);
    }

    private static bool IsAt(string relativePath, string directory)
        => relativePath == directory || relativePath.StartsWith(directory + "/", StringComparison.Ordinal);
}
