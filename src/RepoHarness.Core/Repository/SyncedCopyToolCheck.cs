using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Repository;

/// <summary>Who a command is being run for.</summary>
/// <param name="ServesAnotherMachine">
/// Whether the DssHarness on another machine asked for it, through this one's host agent, rather than
/// somebody typing it here.
/// </param>
public sealed record CommandOrigin(bool ServesAnotherMachine);

/// <summary>
/// Tells somebody who typed a command in a copy the harness synced to a host that the DssHarness running
/// it is older than the newest one published.
/// </summary>
/// <remarks>
/// A host's DssHarness is brought to its dispatcher's version when that machine next reaches it, and not
/// before: nothing on a host can see a dispatcher that has moved on until it does. So a command typed in a
/// copy between the two runs the old build, and says what the old build said - which, across the release
/// that stopped telling a host to create connection data of its own, was advice the arrangement exists to
/// prevent. The newest release published is what can be seen from here, and a dispatcher that keeps up
/// is on it.
///
/// Said only to somebody who typed the command. A command the host agent runs for another machine runs
/// in the same copy, but after an inspection that has just made this build that machine's own, so there
/// is nothing to tell - and a leg would otherwise ask nuget.org once for every run.
/// </remarks>
public sealed class SyncedCopyToolCheck(
    IPublishedToolVersions published,
    IToolIdentityProvider identity,
    CommandOrigin origin,
    IHarnessOutput output)
{
    private readonly IPublishedToolVersions _published = published;
    private readonly IToolIdentityProvider _identity = identity;
    private readonly CommandOrigin _origin = origin;
    private readonly IHarnessOutput _output = output;

    /// <summary>Whether this command has asked already: a command loads its context more than once.</summary>
    private bool _asked;

    /// <summary>
    /// Warns where <paramref name="context"/> is a synced copy, the command was typed here, and a newer
    /// release is published than the one running. Says nothing where that cannot be told.
    /// </summary>
    /// <param name="context">The context the command loaded.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    public async Task WarnWhenBehindAsync(HarnessContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsSyncedCopy || _origin.ServesAnotherMachine || _asked)
        {
            return;
        }

        _asked = true;

        if (!SemanticVersion.TryParse(_identity.Current.Version, out var running))
        {
            return;
        }

        // Said under --verbose, because it costs a command up to the feed's budget and nothing else would
        // account for that pause.
        _output.Detail(
            ToolPackage.Command,
            $"this tree is a copy the harness synced to a host, so nuget.org is asked which {ToolPackage.Id} is newest");

        var newest = await _published.NewestAsync(running, cancellationToken).ConfigureAwait(false);

        if (newest is null)
        {
            _output.Detail(ToolPackage.Command, $"nuget.org did not say which {ToolPackage.Id} is newest, so {running} is not compared");
            return;
        }

        if (SemanticVersion.Compare(running, newest) >= 0)
        {
            return;
        }

        _output.Warn(
            ToolPackage.Command,
            $"this tree is a copy the harness synced to a host, and the {ToolPackage.Id} running here is {running}, "
            + $"while nuget.org has {newest}. The machine that syncs to this one brings this host's {ToolPackage.Id} "
            + "to its own version when it next reaches it, so until then what this one says may trail it: run "
            + "this from that machine.");
    }
}
