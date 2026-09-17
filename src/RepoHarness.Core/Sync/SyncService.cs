using System.Globalization;
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
    private readonly IReadOnlyList<string> _adopt = Adopt ?? [];

    /// <summary>Whether <paramref name="host"/> was named as one to take over.</summary>
    /// <param name="host">The host whose copy is being synced.</param>
    /// <remarks>
    /// Matched on the name alone as well as on the whole spelling, so both <c>--adopt vps</c> and
    /// <c>--adopt "ssh vps"</c> name the same machine and neither is a trap.
    /// </remarks>
    public bool Adopts(HostId host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return _adopt.Any(named =>
            string.Equals(named, host.ToString(), StringComparison.OrdinalIgnoreCase)
            || (host.Name is { Length: > 0 } name && string.Equals(named, name, StringComparison.OrdinalIgnoreCase)));
    }
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
public sealed record SyncResult(
    string Host,
    string Root,
    SyncPlan Plan,
    bool Verified,
    bool Created);

/// <summary>Putting a host's copy of the repository in step with this tree.</summary>
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
    /// One entry per host, not per leg: legs on one host share one tree, and syncing it once per leg
    /// would have their copies racing over the same files.
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
    IHarnessOutput output) : ISyncService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "sync";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IManifestBuilder _manifestBuilder = manifestBuilder;
    private readonly ISyncTransport _localTransport = localTransport;
    private readonly ISyncTransportFactory _transportFactory = transportFactory;
    private readonly Legs.LegsService _legsService = legsService;
    private readonly Git.IGitClient _gitClient = gitClient;
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

        var report = await _legsService.CheckAsync(directory, legNames, here: false, cancellationToken).ConfigureAwait(false);

        var hosts = report.Placements
            .Where(placement => placement is { Runnable: true, Host: not null })
            .Select(placement => placement.Host!)
            .Where(host => host.Host.Kind != Hosts.HostKind.Local)
            .DistinctBy(host => host.Host)
            .ToList();

        if (hosts.Count == 0)
        {
            return CommandOutcome.Ok("no host needs a copy: every runnable leg runs on this machine");
        }

        var context = await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
        var details = new List<string>();

        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = RepositoryPathOf(context.Config, host);
            var transport = _transportFactory.For(host);

            if (pull.Count > 0)
            {
                var brought = await PullAsync(
                        transport, destination, context.Layout.RepositoryRoot, pull, cancellationToken)
                    .ConfigureAwait(false);

                details.Add($"{host.Host}: brought back {brought.Count} file(s)");
                continue;
            }

            var result = await SyncAsync(
                    context.Layout.RepositoryRoot, transport, destination, options, cancellationToken)
                .ConfigureAwait(false);

            details.Add($"{host.Host}: {destination}");
            details.AddRange(result.Plan.Describe(options.DryRun ? SyncVerb.Planned : SyncVerb.Done));
        }

        // Reaching here means every copy was confirmed: a copy that still differed raised from the
        // verification inside the sync, naming the files, and took the whole command with it.
        return CommandOutcome.Ok(
            options.DryRun
                ? $"{hosts.Count} host(s) inspected; nothing was changed"
                : $"{hosts.Count} host(s) in step",
            details);
    }

    /// <summary>
    /// Where a host keeps its copy, as its own configuration declares it.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="host">The host.</param>
    /// <exception cref="HarnessException">The host declares no repository path.</exception>
    public static string RepositoryPathOf(Configuration.HarnessConfig config, Hosts.HostReport host)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(host);

        var name = host.Host.Name;

        Configuration.RemoteHostConfig? declared = host.Host.Kind == Hosts.HostKind.Wsl
            ? config.Hosts.Wsl.GetValueOrDefault(name)
            : config.Hosts.Ssh.GetValueOrDefault(name);

        return declared?.RepositoryPath ?? throw new HarnessException(
            HarnessExit.ConfigInvalid,
            $"Host {host.Host} declares no repositoryPath, so there is nowhere to keep its copy.");
    }

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

        // Asked before the deletion bound. Whose directory this is comes first: told that a sync
        // would remove all of a directory, the reader goes looking for a mistake in the source, when
        // what is actually true is that this is not a copy of the source at all.
        var adopting = options.Adopts(transport.Host);

        if (state == CopyState.Unclaimed && !adopting && !options.DryRun)
        {
            throw new HarnessException(HarnessExit.Refused, Unclaimed(transport, destinationRoot, plan));
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
            plan.RefuseWhenDeletingTooMuch(destination, config.Sync.MaxDeleteFraction);
        }

        if (options.DryRun)
        {
            if (state == CopyState.Unclaimed && !adopting)
            {
                _output.Info(
                    CommandName,
                    $"{transport.Host}: '{destinationRoot}' exists and the harness did not create it; "
                    + $"syncing into it needs '--adopt {transport.Host}'.");
            }

            return new SyncResult(transport.Host.ToString(), destinationRoot, plan, Verified: false, created);
        }

        // Said before it happens, and said whether or not a refusal ever ran. Somebody who reads
        // --adopt in the help and types it on the first run never sees the refusal, and everything
        // that names what taking a directory over costs was inside it.
        if (state == CopyState.Unclaimed)
        {
            _output.Warn(CommandName, $"{transport.Host}: adopting '{destinationRoot}', which this harness did not create.");

            foreach (var line in plan.DescribeLoss(int.MaxValue))
            {
                _output.Warn(CommandName, $"{transport.Host}:   {line}");
            }

            _output.Warn(CommandName, $"{transport.Host}:   replace  {HarnessLayout.DirectoryName}/config.json, with this tree's");

            // Marked before the transfer, not after. Once this run starts deleting, the directory is
            // already neither the checkout it was nor a copy of this tree, and a transfer that stops
            // part way leaves it that way. Marked, the next run finishes the job; unmarked, it would
            // refuse and report a smaller loss than the first one did, because what it had already
            // destroyed no longer shows up in a plan.
            await transport.CreateRootAsync(destinationRoot, adopted: true, cancellationToken).ConfigureAwait(false);
            _output.Info(CommandName, $"{transport.Host}: adopted '{destinationRoot}'");
        }

        await ApplyAsync(context.Layout.RepositoryRoot, transport, destinationRoot, plan, cancellationToken)
            .ConfigureAwait(false);

        // The copy is a git repository because the harness there finds everything through git. Done
        // after the transfer, so a copy that failed part way is not left looking complete.
        await transport.InitialiseRepositoryAsync(destinationRoot, cancellationToken).ConfigureAwait(false);

        await PlaceConfigurationAsync(context.Layout, transport, destinationRoot, cancellationToken)
            .ConfigureAwait(false);

        var verified = await VerifyAsync(transport, destinationRoot, source, exclusions, cancellationToken)
            .ConfigureAwait(false);

        return new SyncResult(transport.Host.ToString(), destinationRoot, plan, verified, created);
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

        return [.. result.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim().TrimEnd('/'))
            .Where(path => path.Length > 0)];
    }

    /// <summary>
    /// Puts this repository's <c>config.json</c> in the copy, and nothing else from
    /// <c>.harness-config</c>.
    /// </summary>
    /// <remarks>
    /// A leg placed on a host runs DssHarness there, and DssHarness in a directory holding no
    /// <c>.harness-config/config.json</c> refuses as not initialised — so without this the copy is a
    /// tree no leg can run in, and the failure arrives as "the host could not be reached" about a
    /// host that answered. Only this one file crosses: the rest of that directory is connection
    /// data, credentials, locks and logs, each of which is local to a machine by design, and the
    /// tracked configuration names hosts only by name.
    /// Written after the transfer and the git initialisation, so a copy that failed part way is
    /// never left looking like one a leg could run in.
    /// </remarks>
    private async Task PlaceConfigurationAsync(
        Repository.HarnessLayout layout,
        ISyncTransport transport,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var relativePath = $"{Repository.HarnessLayout.DirectoryName}/{Repository.HarnessLayout.ConfigFileName}";

        var contents = await _localTransport
            .ReadFileAsync(layout.MainCheckoutRoot, relativePath, cancellationToken)
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
    /// Makes sure there is a copy to write into, and that it is one the harness made.
    /// </summary>
    /// <returns>What was found there.</returns>
    private async Task<CopyState> PrepareCopyAsync(
        ISyncTransport transport,
        string destinationRoot,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!await transport.RootExistsAsync(destinationRoot, cancellationToken).ConfigureAwait(false))
        {
            if (dryRun)
            {
                _output.Info(CommandName, $"{transport.Host}: would create '{destinationRoot}'");
                return CopyState.Created;
            }

            _output.Info(CommandName, $"{transport.Host}: creating '{destinationRoot}'");
            await transport.CreateRootAsync(destinationRoot, adopted: false, cancellationToken).ConfigureAwait(false);

            return CopyState.Created;
        }

        return await transport.IsHarnessCopyAsync(destinationRoot, cancellationToken).ConfigureAwait(false)
            ? CopyState.Harness
            : CopyState.Unclaimed;
    }

    /// <summary>
    /// Why a directory the harness did not make is refused, and what taking it over would cost.
    /// </summary>
    /// <remarks>
    /// A sync deletes whatever the source does not have, so a checkout somebody made by hand may hold
    /// work nothing here knows about. What survives is named exactly, and it is narrower than it
    /// looks: <c>.git</c> and so every commit there, this tool's own directory, the worktrees root,
    /// and whatever <c>sync.neverTransfer</c> names. What git ignores is read from <em>this</em>
    /// tree, by listing the ignored files that exist here — so a build directory that exists only on
    /// the host is ignored by nothing this side can see, and is counted among the deletions like any
    /// other file. It appears in the list below, which is why the list is the thing to read.
    /// </remarks>
    private static string Unclaimed(ISyncTransport transport, string destinationRoot, SyncPlan plan)
    {
        var opening = $"'{destinationRoot}' on {transport.Host} exists and the harness did not create it, "
            + "so sync will not write into it on its own.";

        var take = $"'--adopt {transport.Host}'";

        var configuration = $"Taking it over also replaces {HarnessLayout.DirectoryName}/config.json "
            + "there with this tree's.";

        var survives = $"Its .git and every commit in it, {HarnessLayout.DirectoryName}, the worktrees "
            + "root and whatever sync.neverTransfer names are left alone. Nothing else is: a directory "
            + "that only that host has, a build tree among them, is ignored by nothing this tree can "
            + "see and is deleted like any other file. Name it in sync.neverTransfer first if it "
            + "should stay.";

        if (plan.Overwrites.Count == 0 && plan.Deletes.Count == 0)
        {
            return $"{opening} Taking it over is {take}, and would remove nothing there. "
                + $"{configuration} Nothing has been changed by this run.";
        }

        var counted = $"{plan.Overwrites.Count.ToString(CultureInfo.InvariantCulture)} file(s) would be "
            + $"overwritten and {plan.Deletes.Count.ToString(CultureInfo.InvariantCulture)} deleted there";

        return $"{opening} Taking it over is {take}, and {counted}:\n  "
            + string.Join("\n  ", plan.DescribeLoss())
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

        foreach (var entry in plan.Writes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contents = await _localTransport
                .ReadFileAsync(sourceRoot, entry.Path, cancellationToken)
                .ConfigureAwait(false);

            await transport
                .WriteFileAsync(destinationRoot, entry.Path, contents, cancellationToken)
                .ConfigureAwait(false);

            if (overwritten.Contains(entry.Path))
            {
                _output.Info(CommandName, $"{transport.Host}: overwrote {entry.Path}");
            }
            else
            {
                _output.Detail(CommandName, $"{transport.Host}: wrote {entry.Path}");
            }
        }

        foreach (var path in plan.Deletes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await transport.DeleteFileAsync(destinationRoot, path, cancellationToken).ConfigureAwait(false);

            // Reported at the level a reader sees by default, not behind --verbose: a deletion on
            // another machine is the one thing running the command again cannot undo.
            _output.Info(CommandName, $"{transport.Host}: deleted {path}");
        }
    }

    /// <summary>
    /// Confirms the copy now holds exactly what the source does.
    /// </summary>
    /// <remarks>
    /// A remote tree's identity is its content manifest. Without this the leg that runs next reports
    /// on a tree nobody established, and a transfer that dropped a file would be indistinguishable
    /// from a source that never had it.
    /// </remarks>
    private async Task<bool> VerifyAsync(
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
            return true;
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
