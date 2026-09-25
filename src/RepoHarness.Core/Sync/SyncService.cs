using System.Globalization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>How one sync should behave.</summary>
/// <param name="DryRun">List what would be written and deleted, and change nothing.</param>
/// <param name="Adopt">
/// The hosts whose copy may be taken over although the harness did not create it. Named rather than
/// a plain yes, because one sync reaches every host at once: a run that takes over the directory
/// somebody meant on one machine would otherwise also take over whatever unexpected thing is found
/// at another's repositoryPath, including a mistyped one, without being asked again.
/// </param>
public sealed record SyncOptions(bool DryRun = false, IReadOnlyList<string>? Adopt = null)
{
    /// <summary>
    /// The run whose artifacts to carry to each host instead of syncing the tree, or
    /// <see langword="null"/> for an ordinary sync.
    /// </summary>
    /// <remarks>
    /// A run's artifacts are gitignored, so an ordinary sync withholds them — deliberately, because
    /// the withheld list is what stops a host's own state being written over. This carries exactly
    /// one run's, by name, and nothing else: the narrowest thing that makes a round trip possible
    /// without putting a hole in that rule.
    /// </remarks>
    public string? Artifact { get; init; }

    /// <summary>Refuses <c>--artifact</c> given without a run to carry.</summary>
    /// <exception cref="HarnessException">The option was given and names nothing.</exception>
    /// <remarks>
    /// An empty value is the option asked for, not the option absent, and the difference is the
    /// whole command: read as absent this would fall through to an ordinary sync, which deletes
    /// whatever the host has that this tree does not. A script whose run id variable is unset is
    /// the ordinary way an empty value arrives, and it asked to carry one run's files.
    /// </remarks>
    public void RefuseWhenCarryingAnUnnamedRun()
    {
        if (Artifact is null || !string.IsNullOrWhiteSpace(Artifact))
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.UsageError,
            "--artifact names the run whose artifacts to carry and was given an empty name, so "
            + "there is no run to carry. It is the directory under an action's "
            + $"'{HarnessLayout.ActionArtifactsDirectoryName}', as "
            + "--artifact 20260917-100000-0a1b2c3d. Nothing was changed.");
    }

    /// <summary>Refuses asking for two directions at once.</summary>
    /// <param name="pull">The paths <c>--pull</c> named.</param>
    /// <exception cref="HarnessException">Both directions were asked for.</exception>
    public void RefuseWhenPullingAndCarrying(IReadOnlyList<string> pull)
    {
        ArgumentNullException.ThrowIfNull(pull);

        if (Artifact is not null && pull.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                "--artifact carries a run's artifacts to each host and --pull brings named files "
                + "back from them, so asking for both leaves which direction this runs in undecided. "
                + "Run one, then the other.");
        }
    }

    /// <summary>Whether <paramref name="host"/> was named as one to take over.</summary>
    /// <param name="host">The host whose copy is being synced.</param>
    /// <remarks>
    /// A whole spelling — <c>--adopt "ssh vps"</c> — names one host and nothing else. A bare name is
    /// allowed too, and is checked against every declared host before anything runs, because
    /// <c>hosts.wsl</c> and <c>hosts.ssh</c> are separate and nothing stops the same key appearing
    /// in both: a bare name answering to two machines would take over both when somebody meant one,
    /// which is the blanket permission naming hosts exists to end.
    /// </remarks>
    public bool Adopts(HostId host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return (Adopt ?? []).Any(named =>
            string.Equals(named, host.ToString(), StringComparison.OrdinalIgnoreCase)
            || Named(named, host));
    }

    /// <summary>
    /// Refuses a name no declared host answers to, and a bare name more than one answers to. Called
    /// once, before any host is reached, so a typo is learned before a directory is taken over
    /// rather than never.
    /// </summary>
    /// <param name="declared">Every host this run would reach.</param>
    /// <exception cref="HarnessException">A name matches nothing, or matches more than one host.</exception>
    public void RefuseWhenNamingNoOneHost(IReadOnlyList<HostId> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var spelled = string.Join(", ", declared.Select(host => $"'{host}'"));

        foreach (var named in Adopt ?? [])
        {
            if (declared.Any(host => string.Equals(named, host.ToString(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var answering = declared.Where(host => Named(named, host)).ToList();

            if (answering.Count == 0)
            {
                // A host is two words, and --adopt takes more than one name, so 'ssh vps' typed
                // without quotes arrives as 'ssh' and 'vps'. Said here because this is where that
                // mistake lands, and 'no host is called ssh' does not point at it.
                var unquoted = string.Equals(named, "ssh", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(named, "wsl", StringComparison.OrdinalIgnoreCase)
                    ? " A host's full name is two words and --adopt takes several names, so quote it: "
                        + $"--adopt \"{named} <name>\"."
                    : string.Empty;

                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"--adopt names '{named}', and no host this run reaches is called that. "
                    + $"The hosts are {spelled}.{unquoted} Nothing was changed.");
            }

            if (answering.Count > 1)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"--adopt names '{named}', and {answering.Count} hosts are called that: "
                    + $"{string.Join(", ", answering.Select(host => $"'{host}'"))}. Taking over the wrong "
                    + "one deletes what it holds, so name the one you mean in full. Nothing was changed.");
            }
        }
    }

    private static bool Named(string named, HostId host)
        => host.Name is { Length: > 0 } name && string.Equals(named, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What was found where a host's copy should be.</summary>
internal enum CopyState
{
    /// <summary>This sync made it, so there was nothing in it to lose.</summary>
    Created,

    /// <summary>The harness made it on some earlier run, and it carries its marker.</summary>
    Harness,

    /// <summary>It exists, the harness did not make it, and what it holds is nobody here's to assume about.</summary>
    Unclaimed,

    /// <summary>
    /// A takeover of it began and did not finish. Still nobody's to assume about, and worse than
    /// untouched: part of what was there has already gone, so a plan built now reports less than the
    /// first one did because it can only see what survived.
    /// </summary>
    Interrupted,
}

/// <summary>What one sync did.</summary>
/// <param name="Host">The host whose copy was written.</param>
/// <param name="Root">The copy's root on that host.</param>
/// <param name="Plan">What the sync decided to do.</param>
/// <param name="Verified">
/// Whether the copy was confirmed equal to the source afterwards. True for every sync that returns
/// at all, and false only for a dry run, which changes nothing and so confirms nothing: a copy that
/// does not match raises rather than returning, because a leg must not be run against it and there
/// is nothing a caller could usefully do with a result that says so.
/// </param>
/// <param name="Created">Whether this sync created the copy.</param>
/// <param name="RequiresAdoption">
/// Whether a real run would refuse this copy for want of <c>--adopt</c>. Only a dry run answers
/// yes: a real one raises instead.
/// </param>
public sealed record SyncResult(
    string Host,
    string Root,
    SyncPlan Plan,
    bool Verified,
    bool Created,
    bool RequiresAdoption = false);

/// <summary>Putting a host's copy of a tree in step with it.</summary>
public interface ISyncService
{
    /// <summary>
    /// Syncs every host the selected legs need, and reports what each one did.
    /// </summary>
    /// <param name="directory">The directory the command was invoked in.</param>
    /// <param name="legNames">The legs named with <c>--legs</c>, or null for every declared leg.</param>
    /// <param name="options">How each sync should behave.</param>
    /// <param name="pull">Paths to bring back from each copy instead of syncing to it.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <remarks>
    /// One entry per host, not per leg: the command syncs the tree it runs in, into that tree's copy on
    /// each host, which its legs there share, and syncing it once per leg would have them racing over the
    /// same files.
    /// </remarks>
    Task<CommandOutcome> SyncHostsAsync(
        string directory,
        IReadOnlyList<string>? legNames,
        SyncOptions options,
        IReadOnlyList<string> pull,
        CancellationToken cancellationToken = default);

    /// <summary>Syncs <paramref name="sourceRoot"/> into a copy reached through <paramref name="transport"/>.</summary>
    /// <param name="sourceRoot">The tree to sync from.</param>
    /// <param name="transport">How the copy is reached.</param>
    /// <param name="destinationRoot">Where the copy lives on the far side.</param>
    /// <param name="options">How this sync should behave.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    Task<SyncResult> SyncAsync(
        string sourceRoot,
        ISyncTransport transport,
        string destinationRoot,
        SyncOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Brings named files back from a copy into this tree, verified against a manifest.</summary>
    /// <param name="transport">How the copy is reached.</param>
    /// <param name="sourceRoot">The copy's root on the far side.</param>
    /// <param name="destinationRoot">Where the files land here.</param>
    /// <param name="paths">The paths to bring back, relative to the copy's root.</param>
    /// <param name="cancellationToken">Stops the transfer.</param>
    Task<IReadOnlyList<string>> PullAsync(
        ISyncTransport transport,
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISyncService"/>
public sealed class SyncService(
    IHarnessContextLoader contextLoader,
    IManifestBuilder manifestBuilder,
    ISyncTransport localTransport,
    ISyncTransportFactory transportFactory,
    Legs.LegsService legsService,
    Git.IGitClient gitClient,
    IFileSystem fileSystem,
    Platform.IHostPlatform platform,
    IHarnessOutput output) : ISyncService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "sync";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IManifestBuilder _manifestBuilder = manifestBuilder;
    private readonly ISyncTransport _localTransport = localTransport;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ISyncTransportFactory _transportFactory = transportFactory;
    private readonly Legs.LegsService _legsService = legsService;
    private readonly Git.IGitClient _gitClient = gitClient;
    private readonly Platform.IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <inheritdoc/>
    public async Task<CommandOutcome> SyncHostsAsync(
        string directory,
        IReadOnlyList<string>? legNames,
        SyncOptions options,
        IReadOnlyList<string> pull,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pull);

        // Before the hosts are measured, and before the early return below. Asked for two directions
        // at once, or for a run that kept nothing, this is wrong whether or not any host needs a
        // copy — and answering OK because there happened to be no host is how a mistyped run id
        // reads as a transfer that had nothing to do.
        options.RefuseWhenCarryingAnUnnamedRun();
        options.RefuseWhenPullingAndCarrying(pull);

        var context = await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);

        var carrying = options.Artifact is { } runId ? ArtifactsOf(context.Layout, runId) : [];

        if (options.Artifact is { } named && carrying.Count == 0)
        {
            return CommandOutcome.Failed(
                HarnessExit.UsageError,
                $"no run '{named}' has kept anything: nothing under "
                + $"'{HarnessLayout.RunnerActionsDirectoryRelative}' holds artifacts for it. A step keeps "
                + "what it declares under 'outputs' and asks for with 'persist', and only when it "
                + "passes.");
        }

        // A copy starts no program on a host, so a host is given one whatever it has installed: a host
        // without cmake is still where a runner that builds nothing runs, and where its artifacts go.
        var report = await _legsService
            .CheckAsync(directory, legNames, Legs.LegWorkload.Copy, here: null, cancellationToken)
            .ConfigureAwait(false);

        var hosts = report.Placements
            .Where(placement => placement is { Runnable: true, Host: not null })
            .Select(placement => placement.Host!)
            .Where(host => host.Host.Kind != Hosts.HostKind.Local)
            .DistinctBy(host => host.Host)
            .ToList();

        if (hosts.Count == 0)
        {
            // A host is measured only for a leg this machine cannot take, so one measured and unreachable is
            // a copy some leg needed and nothing made. Counting the runnable hosts alone read that as no
            // host needing one: inside a host's own copy - which reaches no other machine - every leg
            // warned it could not run, and the sync still concluded OK.
            var unreached = report.Hosts
                .Where(host => host.Host.Kind != Hosts.HostKind.Local && !host.Available)
                .Select(host => host.Host.ToString())
                .ToList();

            if (unreached.Count == 0)
            {
                return FailedCheck(report, "no host was reached", context, [])
                    ?? CommandOutcome.Ok("no host needs a copy: every runnable leg runs on this machine");
            }

            // Each host's own reason was warned with the legs it stopped, so it is named here and not said
            // again; in a copy, where that reason is the same for every host, the command ends saying it once.
            return CommandOutcome.Failed(
                HarnessExit.HostUnavailable,
                $"no host a leg is placed on could be reached, so nothing was copied: {string.Join(", ", unreached)}");
        }

        var details = new List<string>();
        var needAdopting = new List<string>();

        // Before any host is reached, so a name that answers to nothing — or to two machines — is
        // refused while nothing has been deleted anywhere.
        options.RefuseWhenNamingNoOneHost([.. hosts.Select(host => host.Host)]);

        // Same reason, for the same kind of mistake: a carry writes into a copy and never makes or
        // takes over one, so a host that has no copy is answered for here rather than after the
        // files have gone to the hosts listed before it.
        if (carrying.Count > 0
            && await NoCopyToCarryIntoAsync(hosts, context, cancellationToken).ConfigureAwait(false)
                is { Count: > 0 } unreachable)
        {
            return CommandOutcome.Failed(
                HarnessExit.Refused,
                $"{unreachable.Count} of {hosts.Count} host(s) hold no copy this harness made, so "
                + $"run '{options.Artifact}' was carried nowhere. A carry writes an existing copy's "
                + "own files and nothing else: it creates no directory and takes none over, because "
                + "a repositoryPath that is a typo would otherwise be filled in rather than "
                + $"noticed. Run '{ToolPackage.Command} sync' first, adding '--adopt \"<host>\"' where a "
                + "directory is already there. Nothing was changed.",
                unreachable);
        }

        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = CopyOf(context, host);
            var transport = _transportFactory.For(host);

            if (carrying.Count > 0)
            {
                if (options.DryRun)
                {
                    details.Add(
                        $"{host.Host}: would carry {carrying.Count} artifact file(s) of run "
                        + $"'{options.Artifact}' into '{destination}'");
                    continue;
                }

                var carried = await CarryAsync(
                        transport, context.Layout.RepositoryRoot, destination, carrying, cancellationToken)
                    .ConfigureAwait(false);

                details.Add($"{host.Host}: carried {carried} artifact file(s) of run '{options.Artifact}'");
                continue;
            }

            if (pull.Count > 0)
            {
                if (options.DryRun)
                {
                    details.Add($"{host.Host}: would bring back {pull.Count} named file(s) from '{destination}'");
                    continue;
                }

                var brought = await PullAsync(
                        transport, destination, context.Layout.RepositoryRoot, pull, cancellationToken)
                    .ConfigureAwait(false);

                details.Add($"{host.Host}: brought back {brought.Count} file(s)");
                continue;
            }

            var result = await SyncAsync(
                    context.Layout.RepositoryRoot, transport, destination, options, cancellationToken)
                .ConfigureAwait(false);

            if (result.RequiresAdoption)
            {
                needAdopting.Add(host.Host.ToString());
            }

            details.Add($"{host.Host}: {destination}");
            details.AddRange(result.Plan.Describe(options.DryRun ? SyncVerb.Planned : SyncVerb.Done));
        }

        // A dry run exits as the run it previews would. Showing the cost and exiting zero makes
        // 'this checkout needs taking over' indistinguishable from 'everything is in step' to
        // anything reading the code, which is what a dry run is for reading. Both halves matter:
        // the list is printed, which is what the refusal sends a reader here for, AND the code says
        // what a real run would answer.
        if (needAdopting.Count > 0)
        {
            return CommandOutcome.Failed(
                HarnessExit.Refused,
                $"{needAdopting.Count} host(s) hold a directory the harness did not create, so a run "
                + $"would refuse them: {string.Join(", ", needAdopting)}. What taking each over would "
                + "cost is listed above. Nothing was changed.",
                details);
        }

        // Reaching here means every copy was confirmed: a copy that still differed raised from the
        // verification inside the sync, naming the files, and took the whole command with it.
        //
        // Said as the direction that ran, because "in step" is a claim about the whole tree that
        // only the tree sync makes good on. A carry writes one run's artifacts and a pull reads a
        // handful of named files; either reported as "in step" tells somebody their host matches
        // this tree, which is the one thing neither of them did.
        return FailedCheck(report, Summary(options, hosts.Count, pull.Count), context, details)
            ?? CommandOutcome.Ok(Summary(options, hosts.Count, pull.Count), details);
    }

    /// <summary>
    /// The sync's conclusion where the check <c>legs</c> makes fails - a leg named with <c>--legs</c> that
    /// no host could take, no selected leg any host could take, or a leg turned away through a defect of
    /// this tool - after whatever the sync could do; <see langword="null"/> where the check passes.
    /// </summary>
    /// <param name="report">Where each selected leg was placed, or why it could not be.</param>
    /// <param name="done">What the sync did, as its conclusion would otherwise have said.</param>
    /// <param name="context">The tree the sync was typed in.</param>
    /// <param name="details">What it did, host by host.</param>
    /// <remarks>
    /// Failed as <c>legs</c> fails, and for its reason: a leg asked for by name is a copy the command was
    /// asked to make, and a host put in step does not answer for one nothing could reach. Such a sync said
    /// how many hosts it had put in step, and exited 0, with the leg's warning the only word of what it
    /// did not do. A leg merely declared on a machine that is switched off is normal, and fails nothing.
    /// </remarks>
    private static CommandOutcome? FailedCheck(
        Legs.LegsReport report,
        string done,
        HarnessContext context,
        IReadOnlyList<string> details)
    {
        var unplaced = report.Placements.Where(placement => !placement.Runnable).Select(placement => placement.Leg.Name).ToList();

        // A tree that declares no leg has nothing to copy, and is done: the check fails a survey that found
        // no leg to run, and a sync with no leg to place has placed everything it was given.
        if (report.Passed || unplaced.Count == 0)
        {
            return null;
        }
        return CommandOutcome.Failed(
            report.Defect is not null ? Verdicts.ExitCodeFor(LegVerdict.Poisoned) : Legs.LegsExit.Unavailable,
            $"{done}; {unplaced.Count} leg(s) could not be placed on any host, so no copy was made for "
                + $"them: {string.Join(", ", unplaced)}",
            details);
    }

    /// <summary>What this run did, in one line, as the direction it ran in.</summary>
    /// <param name="options">What was asked for.</param>
    /// <param name="hosts">How many hosts were reached.</param>
    /// <param name="pulled">How many files <c>--pull</c> named.</param>
    private static string Summary(SyncOptions options, int hosts, int pulled)
    {
        var count = hosts.ToString(CultureInfo.InvariantCulture);

        if (options.DryRun)
        {
            return $"{count} host(s) inspected; nothing was changed";
        }

        if (options.Artifact is { } runId)
        {
            return $"run '{runId}' carried to {count} host(s)";
        }

        return pulled > 0
            ? $"{pulled.ToString(CultureInfo.InvariantCulture)} named file(s) brought back from {count} host(s)"
            : $"{count} host(s) in step";
    }

    /// <summary>Where <paramref name="host"/> keeps the copy of the tree the command runs in: see <see cref="HostCopies"/>.</summary>
    /// <param name="context">The tree, and the configuration that declares the host.</param>
    /// <param name="host">The host.</param>
    /// <exception cref="HarnessException">The host declares no repository path.</exception>
    private string CopyOf(HarnessContext context, Hosts.HostReport host)
        => HostCopies.Of(context.Config, host.Host, context.Layout, context.Layout.RepositoryRoot, _platform.PathComparison);

    /// <inheritdoc/>
    public async Task<SyncResult> SyncAsync(
        string sourceRoot,
        ISyncTransport transport,
        string destinationRoot,
        SyncOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);

        var context = await _contextLoader.LoadAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
        var config = context.Config;

        var exclusions = new SyncExclusions(
            config.Sync,
            config.Worktrees.Root,
            await IgnoredPathsAsync(context.Layout.RepositoryRoot, cancellationToken).ConfigureAwait(false));

        // Before anything is read from the far side, because it is about this tree and costs nothing.
        await exclusions
            .RefuseWhenNoLongerIgnoredAsync(_gitClient, context.Layout.RepositoryRoot, cancellationToken)
            .ConfigureAwait(false);

        // A rule somebody believes is protecting something, doubted before a deletion rests on it.
        // A rooted entry that matches nothing here while the name exists deeper reads, to any
        // reader, as evidence that name is protected.
        var rooted = exclusions.RootedEntriesMatchingNothing(
            _fileSystem,
            context.Layout.RepositoryRoot,
            _platform.PathComparison,
            cancellationToken);

        foreach (var name in rooted.MatchingNothing)
        {
            _output.Warn(
                CommandName,
                $"sync.neverTransfer names '{name}', which is not in this tree's root though the name "
                + $"does exist deeper in it, so this entry protects nothing. Write "
                + $"'{PathPatterns.AnyDepth}{name}' to cover that name wherever it appears.");
        }

        if (rooted.Incomplete is { } unread)
        {
            // Said rather than swallowed. The entries this would have named are exactly the ones a
            // reader believes are protecting something, so "it found none" and "it stopped looking"
            // must not read the same.
            _output.Warn(
                CommandName,
                $"sync.neverTransfer entries could not all be checked against this tree: {unread}");
        }

        // A worktree's copy is claimed for it before anything is written to it: recorded, so that deleting the
        // worktree asks this host to remove it and asks no host that holds none; and refused while another worktree
        // kept under the same name, which still exists, holds it, as the two would be syncing over each other.
        // Before, not after: a first sync that stops part way has already made a copy, and one recorded only once
        // finished would be one nothing removes. A copy recorded that was never made is found not there when the
        // worktree is deleted, and forgotten.
        if (!options.DryRun && context.Layout.IsWorktree(_platform) && transport.Host.Kind != Hosts.HostKind.Local)
        {
            var name = HostCopies.NameOf(context.Layout.RepositoryRoot);
            var claim = new HostCopyEntry(name, transport.Host.ToString(), destinationRoot, Path.GetFullPath(context.Layout.RepositoryRoot));

            if (new HostCopyRecord(_fileSystem, _platform.PathComparison).Claim(context.Layout, claim) is { } holder)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"{transport.Host}: '{destinationRoot}' is the copy of the worktree at '{holder}', which is kept under "
                    + $"the same name, '{name}', and still exists: synced by both, each would replace the tree the other "
                    + "put there. Rename one of them, or delete the other. Nothing was changed.");
            }
        }

        var state = await PrepareCopyAsync(transport, destinationRoot, options.DryRun, cancellationToken)
            .ConfigureAwait(false);

        var created = state == CopyState.Created;

        var source = await _manifestBuilder
            .BuildAsync(context.Layout.RepositoryRoot, exclusions.IsWithheldFromTransfer, cancellationToken)
            .ConfigureAwait(false);

        var destination = created
            ? SyncManifest.Empty(destinationRoot)
            : await transport
                .ReadManifestAsync(destinationRoot, Withheld(exclusions), cancellationToken)
                .ConfigureAwait(false);

        var plan = SyncPlan.Between(source, destination, exclusions);

        // Whose directory this is comes first. Told that a sync would remove all of a directory, the
        // reader goes looking for a mistake in the source, when what is actually true is that this is
        // not a copy of the source at all.
        var mine = state is CopyState.Created or CopyState.Harness;
        var adopting = !mine && options.Adopts(transport.Host);

        if (!mine && !adopting && !options.DryRun)
        {
            throw new HarnessException(HarnessExit.Refused, Unclaimed(transport, destinationRoot, plan, state, destination.Links));
        }

        // Said on every sync, not only on a takeover. A copy this tool made can gain a link
        // afterwards — a build script pointing a directory at scratch space is the ordinary way —
        // and from then on every build and every test writes through it, to a place outside the
        // directory anybody named, reported as an ordinary write. The takeover list covers the
        // first sync into somebody else's directory; this covers all the rest.
        foreach (var link in plan.WritesThrough(destination.Links))
        {
            _output.Warn(
                CommandName,
                $"{transport.Host}: writing through '{link}', which is a link: what it points at is "
                + $"outside '{destinationRoot}' and is not in any list this command can build.");
        }

        // Not on a dry run, which changes nothing there is a bound to protect. The bound's own
        // message says to run with --dry-run to see the list, and until now that refused in exactly
        // the same words rather than showing it.
        //
        // It does still bound an adoption. A directory that exists and carries no marker is exactly
        // what a mistyped repositoryPath produces, which is the case this bound was written for: the
        // path that was meant to be a checkout is a home directory, and every other project under it
        // is what the source does not have. Two gates for that is the point of having one.
        if (!options.DryRun)
        {
            plan.RefuseWhenDeletingTooMuch(destination, config.Sync.MaxDeleteFraction, adopting);
        }

        if (options.DryRun)
        {
            // The same words the refusal uses, because this is where the refusal sends the reader
            // for the whole of it. A stopped takeover told apart from an untouched directory
            // matters most here: the plan below is built from what survived, and a dry run that
            // called it an untouched directory would present that plan as the whole cost.
            if (!mine && !adopting)
            {
                _output.Info(
                    CommandName,
                    Unclaimed(transport, destinationRoot, plan, state, destination.Links));
            }

            return new SyncResult(
                transport.Host.ToString(),
                destinationRoot,
                plan,
                Verified: false,
                created,
                RequiresAdoption: !mine && !adopting);
        }

        // Said before it happens, and said whether or not a refusal ever ran. Somebody who reads
        // --adopt in the help and types it on the first run never sees the refusal, and everything
        // that names what taking a directory over costs was inside it.
        if (adopting)
        {
            _output.Warn(CommandName, $"{transport.Host}: adopting '{destinationRoot}', which this harness did not create.");

            foreach (var line in plan.DescribeLoss(int.MaxValue, destination.Links))
            {
                _output.Warn(CommandName, $"{transport.Host}:   {line}");
            }

            _output.Warn(CommandName, $"{transport.Host}:   replace  {HarnessLayout.DirectoryName}/config.json, with this tree's");
            _output.Warn(CommandName, $"{transport.Host}:   mirror   {HarnessLayout.RunnerActionsDirectoryRelative}, to this tree's actions");

            // Marked as begun before anything is deleted, and marked as finished only once the copy
            // is one. A takeover that stops part way is neither the checkout somebody had nor a copy
            // of this tree, and both of the obvious markings are wrong about it: unmarked, the next
            // run refuses and reports a smaller loss than this one did, because what has gone no
            // longer shows up in a plan; marked complete, the next ordinary build or test — which
            // never carries an adopt list — would quietly delete the rest with nobody asked at all.
            await transport
                .CreateRootAsync(destinationRoot, CopyMark.AdoptionStopped, cancellationToken)
                .ConfigureAwait(false);
        }

        await ApplyAsync(context.Layout.RepositoryRoot, transport, destinationRoot, plan, cancellationToken)
            .ConfigureAwait(false);

        // The copy is a git repository because the harness there finds everything through git. Done
        // after the transfer, so a copy that failed part way is not left looking complete.
        await transport.InitialiseRepositoryAsync(destinationRoot, cancellationToken).ConfigureAwait(false);

        await PlaceConfigurationAsync(context, transport, destinationRoot, cancellationToken)
            .ConfigureAwait(false);

        // The copy's own record of which files are its own: every file this sync carried, and the
        // configuration it placed. Written without staging one, a copy's index named nothing, so a build
        // there fingerprinted no inputs and started every build after the first from clean, and every
        // guard watching the inputs watched nothing. On every sync, so a copy made before this is put
        // right by the next, whatever that one carries. A file written through a link in the copy is
        // outside it, where git cannot hold it; that write was warned of above, and the verification
        // below says the copy does not hold it.
        await transport
            .IndexAsync(
                destinationRoot,
                [
                    .. source.Paths.Where(path => !BeyondALink(path, destination.Links)),
                    $"{HarnessLayout.DirectoryName}/{HarnessLayout.ConfigFileName}",
                ],
                cancellationToken)
            .ConfigureAwait(false);

        // Throws when the copy does not match, so reaching the next line is what verified means.
        await VerifyAsync(transport, destinationRoot, source, exclusions, cancellationToken)
            .ConfigureAwait(false);

        // Marked finished only once the copy has been shown to be one, which is why this sits after
        // the verification and not before it. A takeover whose verification failed never reaches
        // here, so the mark still says it stopped part way and the next run asks again rather than
        // treating a directory whose content nobody could confirm as this tool's own.
        if (adopting)
        {
            await transport
                .CreateRootAsync(destinationRoot, CopyMark.Complete, cancellationToken)
                .ConfigureAwait(false);

            _output.Info(CommandName, $"{transport.Host}: adopted '{destinationRoot}'");
        }

        return new SyncResult(transport.Host.ToString(), destinationRoot, plan, Verified: true, created);
    }

    /// <summary>
    /// The top of everything git ignores in <paramref name="root"/>, as paths relative to it.
    /// </summary>
    /// <param name="root">The source tree.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <exception cref="HarnessException">
    /// git could not be asked what it ignores. A refusal rather than an empty list: an empty list
    /// reads as "this repository ignores nothing", and the sync that follows would copy every
    /// ignored file in the tree to the host.
    /// </exception>
    /// <remarks>
    /// <c>--directory</c> so an ignored directory is named once rather than file by file, which is
    /// what keeps this cheap on a tree holding a build directory or a package cache.
    /// </remarks>
    private async Task<IReadOnlyList<string>> IgnoredPathsAsync(string root, CancellationToken cancellationToken)
    {
        var result = await _gitClient
            .RunAsync(
                root,
                ["ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z"],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"What git ignores in '{root}' could not be listed, so a sync cannot tell which files are "
                + $"local to this machine: {result.FailureMessage}");
        }

        // Each name as git gives it, less the slash that marks a directory. Trimmed as well, a name
        // beginning or ending with a space became one no file has, and the ignored file under it was
        // copied to the host.
        return [.. result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.TrimEnd('/'))
            .Where(path => path.Length > 0)];
    }

    /// <summary>Whether <paramref name="path"/> is under one of the copy's links.</summary>
    /// <param name="path">A path relative to the copy's root, with forward separators.</param>
    /// <param name="links">The copy's links, as its manifest names them before the sync wrote anything.</param>
    /// <remarks>
    /// Under one, and not at one: a file written at a link's own name replaces the link, and is then a
    /// file of the copy like any other. Left out of the index, it went unwatched by every build and
    /// guard there until the next sync.
    /// </remarks>
    private static bool BeyondALink(string path, IReadOnlyList<string> links)
        => links.Any(link => path.StartsWith(link + "/", StringComparison.Ordinal));

    /// <summary>
    /// Puts the <c>config.json</c> this command read into the copy.
    /// </summary>
    /// <remarks>
    /// A leg placed on a host runs DssHarness there, and DssHarness in a directory holding no
    /// <c>.harness-config/config.json</c> refuses as not initialised — so without this the copy is a
    /// tree no leg can run in, and the failure arrives as "the host could not be reached" about a
    /// host that answered. The file is the one this command was configured from: the synced tree's
    /// own, which for a worktree is that worktree's. Read from the main checkout instead, as it was,
    /// a worktree's remote legs ran with a configuration the worktree did not have.
    /// Written after the transfer and the git initialisation, so a copy that failed part way is
    /// never left looking like one a leg could run in. The rest of the harness's directory crosses
    /// only as far as <see cref="HarnessDirectorySync"/> lets it.
    /// </remarks>
    private async Task PlaceConfigurationAsync(
        Repository.HarnessContext context,
        ISyncTransport transport,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var relativePath = $"{Repository.HarnessLayout.DirectoryName}/{Repository.HarnessLayout.ConfigFileName}";

        // The tree's file and the main checkout's fallback are both '<root>/.harness-config/config.json'.
        var configRoot = Path.GetDirectoryName(Path.GetDirectoryName(context.ConfigFile))!;

        var contents = await _localTransport
            .ReadFileAsync(configRoot, relativePath, cancellationToken)
            .ConfigureAwait(false);

        await transport.WriteFileAsync(destinationRoot, relativePath, contents, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> PullAsync(
        ISyncTransport transport,
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(paths);

        var brought = new List<string>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Checked against the hash the far side took before sending, inside ReadFileAsync: an
            // artefact carried host to host is evidence that a binary built there runs here, and
            // evidence nobody checked is not evidence.
            var contents = await transport.ReadFileAsync(sourceRoot, path, cancellationToken).ConfigureAwait(false);
            var expected = FileContentHash.Of(contents);

            await _localTransport
                .WriteFileAsync(destinationRoot, path, contents, cancellationToken)
                .ConfigureAwait(false);

            // And again after it is written, because a file that arrived intact and landed truncated
            // is still not the artefact somebody is about to run.
            var landed = await _localTransport.ReadFileAsync(destinationRoot, path, cancellationToken).ConfigureAwait(false);
            var actual = FileContentHash.Of(landed);

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"'{path}' did not land intact from {transport.Host}: it arrived as {expected} and "
                    + $"was written as {actual}.");
            }

            brought.Add(path);
        }

        return brought;
    }

    /// <summary>
    /// Every file one run kept, relative to the tree root, across every action and every leg.
    /// </summary>
    /// <param name="layout">Resolved paths for this repository.</param>
    /// <param name="runId">The run whose artifacts to find.</param>
    /// <remarks>
    /// Found by walking the actions directory for an <c>artifacts/&lt;run id&gt;</c>, at whatever
    /// depth the author grouped the action to. Links are not followed: a link inside a tree makes
    /// the walk unbounded, and one leaving it would carry files the tree does not contain to another
    /// machine.
    /// </remarks>
    private IReadOnlyList<string> ArtifactsOf(HarnessLayout layout, string runId)
    {
        var actions = layout.RunnerActionsDirectory;

        if (!_fileSystem.DirectoryExists(actions))
        {
            return [];
        }

        var found = new List<string>();
        Collect(actions, 0);
        return [.. found.Order(StringComparer.Ordinal)];

        void Collect(string directory, int depth)
        {
            // Deep enough for any grouping somebody writes by hand, and bounded so a tree that is
            // not one cannot spend the command.
            if (depth > 32)
            {
                return;
            }

            foreach (var child in _fileSystem.EnumerateDirectories(directory))
            {
                if (LinkPaths.IsLink(_fileSystem, child, _platform.PathComparison))
                {
                    continue;
                }

                // Neither of the two directories an action owns holds actions, and no action may
                // be called either name. The artifacts one holds runs, and walking it would treat
                // each run directory as an action; the build one holds the working space of runs in
                // flight and of any that was killed before it could clear its own, which is a whole
                // build tree to enumerate for something that cannot be in it.
                if (Runners.ActionPath.Reserved(Path.GetFileName(child)))
                {
                    continue;
                }

                var kept = Path.Combine(child, HarnessLayout.ActionArtifactsDirectoryName, runId);

                if (_fileSystem.DirectoryExists(kept))
                {
                    foreach (var file in _fileSystem.EnumerateFiles(kept, recursive: true))
                    {
                        found.Add(PathPatterns.Normalize(
                            Path.GetRelativePath(layout.RepositoryRoot, file)));
                    }
                }

                Collect(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// Writes each of <paramref name="paths"/> into the copy at the same relative place, verified
    /// on arrival.
    /// </summary>
    /// <param name="transport">How the copy is reached.</param>
    /// <param name="sourceRoot">This tree's root.</param>
    /// <param name="destinationRoot">The copy's root on the far side.</param>
    /// <param name="paths">What to carry, relative to the tree root.</param>
    /// <param name="cancellationToken">Stops the transfer.</param>
    /// <returns>How many files landed.</returns>
    /// <remarks>
    /// The same relative place, which is what makes a consuming step's path work unchanged on the
    /// far side: the run and the leg that produced it are already in it. Verified after writing for
    /// the reason a pull is — an artefact carried host to host is evidence that something built
    /// there runs here, and evidence nobody checked is not evidence.
    /// </remarks>
    private async Task<int> CarryAsync(
        ISyncTransport transport,
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var landed = new List<string>();

        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var contents = await _localTransport
                    .ReadFileAsync(sourceRoot, path, cancellationToken)
                    .ConfigureAwait(false);

                var expected = FileContentHash.Of(contents);

                await transport.WriteFileAsync(destinationRoot, path, contents, cancellationToken)
                    .ConfigureAwait(false);

                // Counted as written before it is verified, because what has to be taken back is
                // what reached the far side, and a file that arrived wrong is one of those.
                landed.Add(path);

                var arrived = await transport.ReadFileAsync(destinationRoot, path, cancellationToken)
                    .ConfigureAwait(false);

                var actual = FileContentHash.Of(arrived);

                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"'{path}' did not land intact on {transport.Host}: it was sent as {expected} and "
                        + $"arrived as {actual}.");
                }
            }
        }
        catch (Exception)
        {
            await TakeBackAsync(transport, destinationRoot, landed).ConfigureAwait(false);
            throw;
        }

        return landed.Count;
    }

    /// <summary>
    /// Removes what a carry had already written, after that carry stopped part way.
    /// </summary>
    /// <param name="transport">How the copy is reached.</param>
    /// <param name="destinationRoot">The copy's root on the far side.</param>
    /// <param name="written">What reached it, relative to the tree root.</param>
    /// <remarks>
    /// A run's artifacts are read by naming a path, and a directory holding two thirds of what the
    /// producer kept is the same path holding fewer files: the step that reads it measures less
    /// than was built and passes, and nothing anywhere says which of the two happened. The carry
    /// therefore lands whole or not at all. It stops loudly on this side either way; taking the
    /// part back is what stops it being quiet on the other.
    /// <para>
    /// Not cancellable. The removal exists because the transfer stopped, and stopping it by the
    /// same Ctrl-C is exactly the case that would leave the partial set in place.
    /// </para>
    /// </remarks>
    private async Task TakeBackAsync(
        ISyncTransport transport,
        string destinationRoot,
        IReadOnlyList<string> written)
    {
        if (written.Count == 0)
        {
            return;
        }

        var stayed = new List<string>();

        foreach (var path in written)
        {
            try
            {
                await transport.DeleteFileAsync(destinationRoot, path, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HarnessException or IOException or UnauthorizedAccessException)
            {
                stayed.Add(path);
            }
        }

        if (stayed.Count == 0)
        {
            _output.Warn(
                CommandName,
                $"{transport.Host}: the carry stopped part way, so the {written.Count} file(s) it had "
                + "already written were taken back. That run has nothing there.");

            return;
        }

        // Said, not swallowed. This is the one outcome where a later step can read a run's
        // artifacts and be measuring less than the producer kept, so the files are named.
        _output.Warn(
            CommandName,
            $"{transport.Host}: the carry stopped part way and {stayed.Count} of the "
            + $"{written.Count} file(s) it had written could not be taken back, so that run's "
            + "artifacts are there in part and a step reading them would measure less than was "
            + $"kept. Remove them before running anything that reads them: {string.Join(", ", stayed)}");
    }

    /// <summary>
    /// The hosts among <paramref name="hosts"/> that hold no copy this harness made, each with why.
    /// </summary>
    /// <param name="hosts">Every host this run would reach.</param>
    /// <param name="context">The tree carried from, and the configuration, for where each host keeps its copy of that tree.</param>
    /// <param name="cancellationToken">Stops the questions.</param>
    /// <remarks>
    /// The same gate an ordinary sync passes through, asked for a carry as well. Without it a carry
    /// is the one write to a host that skips it: it would create the whole path under a mistyped
    /// repositoryPath and report success, and it would leave the directory it made unmarked, so the
    /// next ordinary sync refuses as "a directory the harness did not create" a directory this tool
    /// made itself minutes earlier.
    /// </remarks>
    private async Task<IReadOnlyList<string>> NoCopyToCarryIntoAsync(
        IReadOnlyList<Hosts.HostReport> hosts,
        HarnessContext context,
        CancellationToken cancellationToken)
    {
        var refused = new List<string>();

        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = CopyOf(context, host);

            var found = await _transportFactory.For(host)
                .InspectAsync(destination, cancellationToken)
                .ConfigureAwait(false);

            var why = !found.Exists
                ? "is not there"
                : StateOf(found.Mark) switch
                {
                    CopyState.Harness => null,
                    CopyState.Interrupted => "was being taken over and the run stopped before it "
                        + "finished, so it is neither the checkout it was nor a copy of this tree",
                    _ => "exists and the harness did not create it",
                };

            if (why is not null)
            {
                refused.Add($"{host.Host}: '{destination}' {why}");
            }
        }

        return refused;
    }

    /// <summary>
    /// Makes sure there is a copy to write into, and that it is one the harness made.
    /// </summary>
    /// <returns>What was found there.</returns>
    private async Task<CopyState> PrepareCopyAsync(
        ISyncTransport transport,
        string destinationRoot,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // Both halves in one question. Asked separately they are two round trips over ssh for
        // something the far side answers in one, and the directory can change between them — so the
        // mark that decides whether this may delete could be describing a directory other than the
        // one that was found to exist.
        var found = await transport.InspectAsync(destinationRoot, cancellationToken).ConfigureAwait(false);

        if (!found.Exists)
        {
            if (dryRun)
            {
                _output.Info(CommandName, $"{transport.Host}: would create '{destinationRoot}'");
                return CopyState.Created;
            }

            _output.Info(CommandName, $"{transport.Host}: creating '{destinationRoot}'");
            await transport.CreateRootAsync(destinationRoot, CopyMark.Complete, cancellationToken).ConfigureAwait(false);

            return CopyState.Created;
        }

        return StateOf(found.Mark);
    }

    /// <summary>What a copy's marker says about whose directory it is.</summary>
    /// <param name="mark">What the far side recorded there.</param>
    private static CopyState StateOf(CopyMark mark) => mark switch
    {
        CopyMark.Complete => CopyState.Harness,
        CopyMark.AdoptionStopped => CopyState.Interrupted,
        _ => CopyState.Unclaimed,
    };

    /// <summary>
    /// Why a directory the harness did not make is refused, and what taking it over would cost.
    /// </summary>
    /// <remarks>
    /// A sync deletes whatever the source does not have, so a checkout somebody made by hand may hold
    /// work nothing here knows about. What survives is named exactly, and it is narrower than it
    /// looks: <c>.git</c> and so every commit there, this tool's own state in its directory — though
    /// the <c>config.json</c> there is replaced with this tree's and its actions are made to match
    /// this tree's, keeping each action's own build and artifacts — the worktrees root, and
    /// whatever <c>sync.neverTransfer</c> names. What git ignores is read from <em>this</em>
    /// tree, by listing the ignored files that exist here — so a build directory that exists only on
    /// the host is ignored by nothing this side can see, and is counted among the deletions like any
    /// other file. It appears in the list below, which is why the list is the thing to read.
    /// </remarks>
    private static string Unclaimed(
        ISyncTransport transport,
        string destinationRoot,
        SyncPlan plan,
        CopyState state,
        IReadOnlyList<string> links)
    {
        // An interrupted takeover is worse than an untouched directory, and the difference has to be
        // said: the list below is built from what is there now, and what an earlier run already
        // removed is not in it and cannot be.
        var opening = state == CopyState.Interrupted
            ? $"'{destinationRoot}' on {transport.Host} was being taken over and the run stopped before "
                + "it finished, so it is neither the checkout it was nor a copy of this tree. What that "
                + "run had already removed is gone and is not in the list below."
            : $"'{destinationRoot}' on {transport.Host} exists and the harness did not create it, "
                + "so sync will not write into it on its own.";

        var take = $"'--adopt \"{transport.Host}\"'";

        var configuration = $"Taking it over also replaces {HarnessLayout.DirectoryName}/config.json "
            + $"there with this tree's, and makes {HarnessLayout.RunnerActionsDirectoryRelative} there match this tree's, action by action.";

        var survives = $"Its .git and every commit in it, the harness's own state in {HarnessLayout.DirectoryName} "
            + "- connection data, secrets, runner values, locks, runs, and each action's own build and "
            + "artifacts - the worktrees root and whatever sync.neverTransfer names are left alone. Nothing else "
            + "is: a directory that only that host has, a build tree among them, is ignored by nothing "
            + "this tree can see and is deleted like any other file. A link there is never followed "
            + "and never deleted, so nothing behind one is in this list — but a file this tree has "
            + "under a linked directory is written where that link points, outside the directory "
            + "named here. Name anything that should stay in sync.neverTransfer first.";

        // Which verb is true depends on whether a takeover was ever begun, exactly as the opening
        // does. Told that finishing is all that is left, somebody looking at a checkout nothing has
        // touched goes hunting for the run that started on it.
        var (verb, cost) = state == CopyState.Interrupted
            ? ("Finishing it", "would remove nothing further")
            : ("Taking it over", "would remove nothing that is there");

        if (plan.Overwrites.Count == 0 && plan.Deletes.Count == 0 && links.Count == 0)
        {
            return $"{opening} {verb} is {take}, and {cost}. "
                + $"{configuration} Nothing has been changed by this run.";
        }

        var counted = $"{plan.Overwrites.Count.ToString(CultureInfo.InvariantCulture)} file(s) would be "
            + $"overwritten and {plan.Deletes.Count.ToString(CultureInfo.InvariantCulture)} deleted there";

        return $"{opening} {verb} is {take}, and {counted}:\n  "
            + string.Join("\n  ", plan.DescribeLoss(links: links))
            + $"\n{configuration} {survives} Run with '--dry-run' to see all of it. Nothing has been "
            + "changed by this run.";
    }

    private async Task ApplyAsync(
        string sourceRoot,
        ISyncTransport transport,
        string destinationRoot,
        SyncPlan plan,
        CancellationToken cancellationToken)
    {
        // A write that replaces something is as unrecoverable as a deletion, and running the command
        // again does not bring back an edit nobody committed either. Reported like one rather than
        // only under --verbose, where a write that costs nothing belongs.
        var overwritten = new HashSet<string>(plan.Overwrites, StringComparer.Ordinal);

        // Carried in batches, because over a connection one exchange is one session, and a session costs
        // what opening one costs rather than what its bytes cost. A file at a time, a consumer's first
        // sync of a worktree's copy opened a session per file and outlasted the host's wake.
        var batch = new List<SyncFileContent>();
        var held = 0L;

        foreach (var entry in plan.Writes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contents = await _localTransport
                .ReadFileAsync(sourceRoot, entry.Path, cancellationToken)
                .ConfigureAwait(false);

            // Sent before this file joins it, so a batch never holds more than the budget: a file larger
            // than the budget on its own then crosses in a batch of its own, which is what carrying it at
            // all requires.
            if (batch.Count > 0
                && (held + contents.LongLength > SyncServe.LargestBatch || batch.Count >= SyncServe.MostFilesInABatch))
            {
                await CarryBatchAsync().ConfigureAwait(false);
            }

            batch.Add(new SyncFileContent(entry.Path, contents));
            held += contents.LongLength;
        }

        await CarryBatchAsync().ConfigureAwait(false);

        // Said once the files are there, and in the order they were carried: a line saying a file was
        // written before the write is answered for would outlive a batch that failed part way.
        async Task CarryBatchAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            await transport.WriteFilesAsync(destinationRoot, batch, cancellationToken).ConfigureAwait(false);

            foreach (var file in batch)
            {
                if (overwritten.Contains(file.Path))
                {
                    _output.Info(CommandName, $"{transport.Host}: overwrote {file.Path}");
                }
                else
                {
                    _output.Detail(CommandName, $"{transport.Host}: wrote {file.Path}");
                }
            }

            batch.Clear();
            held = 0;
        }

        foreach (var path in plan.Deletes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await transport.DeleteFileAsync(destinationRoot, path, cancellationToken).ConfigureAwait(false);

            // Reported at the level a reader sees by default, not behind --verbose: a deletion on
            // another machine is the one thing running the command again cannot undo.
            _output.Info(CommandName, $"{transport.Host}: deleted {path}");
        }

        // A manifest holds files, so deleting every file a directory had leaves the directory. It
        // cannot be seen here: git rm takes a directory with its last file, so the tree this side
        // looks right and only the host keeps the husk. Measured on a consumer's host after a wave
        // of twenty deletions: ten directories left, eight of them holding nothing at all, and the
        // checks that read that tree refused it for having a directory nothing in it answers to.
        foreach (var directory in await transport
            .RemoveEmptyDirectoriesAsync(destinationRoot, plan.Emptied, cancellationToken)
            .ConfigureAwait(false))
        {
            if (directory.Removed)
            {
                _output.Info(CommandName, $"{transport.Host}: removed {directory.Path}/, which the deletion emptied");
                continue;
            }

            // Warned, not passed over. The copy no longer matches this tree, and the only other way
            // anybody learns is a check failing on that host later with nothing naming the cause.
            _output.Warn(
                CommandName,
                $"{transport.Host}: {directory.Path}/ held only files this sync does not manage, so it "
                + $"stayed although the deletion emptied it of everything else: {string.Join(", ", directory.Held)}. "
                + "The copy there differs from this tree until somebody removes it.");
        }
    }

    /// <summary>
    /// Confirms the copy now holds exactly what the source does.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The copy does not match. Raised rather than answered, so returning at all is what being
    /// verified means: there is nothing a caller could usefully do with a copy a leg must not run
    /// against, and everything that happens after this point is allowed to assume it matched.
    /// </exception>
    /// <remarks>
    /// A remote tree's identity is its content manifest. Without this the leg that runs next reports
    /// on a tree nobody established, and a transfer that dropped a file would be indistinguishable
    /// from a source that never had it.
    /// </remarks>
    private async Task VerifyAsync(
        ISyncTransport transport,
        string destinationRoot,
        SyncManifest source,
        SyncExclusions exclusions,
        CancellationToken cancellationToken)
    {
        var after = await transport
            .ReadManifestAsync(destinationRoot, Withheld(exclusions), cancellationToken)
            .ConfigureAwait(false);

        var differences = SyncPlan.Between(source, after, exclusions);

        if (differences.IsUpToDate)
        {
            return;
        }

        var named = differences.Writes
            .Select(entry => entry.Path)
            .Concat(differences.Deletes)
            .Take(5)
            .ToList();

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"The copy at '{destinationRoot}' on {transport.Host} does not match this tree after the "
            + $"sync: {differences.Writes.Count + differences.Deletes.Count} file(s) still differ, "
            + $"including {string.Join(", ", named)}. Nothing should be run against it.");
    }

    /// <summary>
    /// What the far side must not walk. The withheld list only: an excluded path is still deleted
    /// from a copy, so the copy's manifest has to report it.
    /// </summary>
    private static IReadOnlyList<string> Withheld(SyncExclusions exclusions) => exclusions.Withheld;
}
