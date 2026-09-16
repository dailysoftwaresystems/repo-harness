using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Legs;

/// <summary>The exit code <c>legs</c> uses for an answer of "no".</summary>
/// <remarks>
/// From the range reserved for a command's own contract, like <see cref="Git.VerifyGitStatus"/>: a leg
/// that cannot run is what the command was asked to find out, not a failure of the command.
/// </remarks>
public static class LegsExit
{
    /// <summary>A leg named with <c>--legs</c> cannot run, or no selected leg can.</summary>
    public const int Unavailable = 1;
}

/// <summary>What checking the selected legs found.</summary>
/// <param name="Placements">Where each selected leg runs, or why it cannot, in selection order.</param>
/// <param name="Hosts">Every host that was measured.</param>
/// <param name="Named">Whether the legs were named with <c>--legs</c>.</param>
public sealed record LegsReport(IReadOnlyList<LegPlacement> Placements, IReadOnlyList<HostReport> Hosts, bool Named)
{
    /// <summary>
    /// Whether the check passed. A leg named with <c>--legs</c> that cannot run fails it, because it
    /// was asked for; one that was merely declared does not, because a switched-off machine is normal.
    /// Either way at least one leg must be able to run, or nothing would.
    /// </summary>
    public bool Passed => Placements.Any(placement => placement.Runnable)
        && (!Named || Placements.All(placement => placement.Runnable));
}

/// <summary>
/// Finds where each selected leg can run before anything runs. It measures the hosts that could run
/// them, bringing DssHarness on each to this machine's build, and places each leg on the first host
/// that provides its operating system, its processor and its emulator.
/// </summary>
public sealed class LegsService(IHarnessContextLoader contextLoader, IHostInspector inspector, IHarnessOutput output)
{
    /// <summary>The command's name, which prefixes what it reports.</summary>
    public const string CommandName = "legs";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostInspector _inspector = inspector;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// Checks the legs <paramref name="legNames"/> selects, or every leg when <c>--legs</c> was left out and
    /// <paramref name="legNames"/> is <see langword="null"/>.
    /// </summary>
    public async Task<LegsReport> CheckAsync(
        string directory,
        IReadOnlyList<string>? legNames,
        bool here = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var context = await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
        var config = context.Config;
        var selection = LegSelection.Resolve(config, legNames);
        var emulators = EmulatorsUsedBy(config, selection);
        var candidates = selection.Legs.Select(leg => (leg, Hosts: LegPlacement.Candidates(config, leg.Leg, here))).ToList();
        var reports = new Dictionary<HostId, HostReport>();

        // This machine costs nothing to reach, so it is measured first. Other hosts are measured only
        // for the legs it cannot run, and all of those hosts at once.
        if (candidates.Any(entry => entry.Hosts.Contains(HostId.Local)))
        {
            reports[HostId.Local] = await _inspector.InspectAsync(context, HostId.Local, emulators, cancellationToken).ConfigureAwait(false);
        }

        var remote = candidates
            .Where(entry => !entry.Hosts.Contains(HostId.Local) || LegPlacement.Obstacle(entry.leg.Leg, reports[HostId.Local]) is not null)
            .SelectMany(entry => entry.Hosts)
            .Where(host => host.Kind != HostKind.Local)
            .Distinct()
            .ToList();

        foreach (var report in await InspectAllAsync(context, remote, emulators, cancellationToken).ConfigureAwait(false))
        {
            reports[report.Host] = report;
        }

        var placements = selection.Legs.Select(leg => LegPlacement.Place(config, leg, reports, here)).ToList();

        // Each leg that cannot run is its own warning, naming it and saying why, while the others go on.
        foreach (var placement in placements.Where(placement => !placement.Runnable))
        {
            _output.Warn(CommandName, $"leg '{placement.Leg.Name}' cannot run: {placement.Reason}");
        }

        return new LegsReport(placements, [.. reports.Values], selection.Named);
    }

    /// <summary>
    /// Measures <paramref name="hosts"/> all at once. When any of them fails, what every other one changed is
    /// still reported before a failure is raised: hosts are measured together, so one may be updated while
    /// another refuses the run, and that update happened all the same.
    /// </summary>
    private async Task<HostReport[]> InspectAllAsync(
        HarnessContext context,
        List<HostId> hosts,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        var inspections = hosts.Select(host => InspectOneAsync(context, host, emulators, cancellationToken)).ToList();

        try
        {
            return await Task.WhenAll(inspections).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ReportThenThrow(hosts, inspections, cancellationToken);
            throw;
        }
    }

    /// <summary>Measures one host, keeping whatever it throws in the task instead of throwing it at the caller.</summary>
    private async Task<HostReport> InspectOneAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
        => await _inspector.InspectAsync(context, host, emulators, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reports what the measurements that finished changed, warns about every failure but one, and raises that
    /// one. A refusal carries its remedy, such as the command that updates this machine, so it is the one raised.
    /// </summary>
    [DoesNotReturn]
    private void ReportThenThrow(List<HostId> hosts, List<Task<HostReport>> inspections, CancellationToken cancellationToken)
    {
        foreach (var report in inspections.Where(inspection => inspection.IsCompletedSuccessfully).Select(inspection => inspection.Result))
        {
            foreach (var action in report.Actions)
            {
                _output.Info(CommandName, $"{report.Host}: {action}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var failures = hosts
            .Zip(inspections)
            .Where(pair => !pair.Second.IsCompletedSuccessfully)
            .Select(pair => (Host: pair.First, Exception: pair.Second.Exception?.InnerException
                ?? new OperationCanceledException($"measuring {pair.First} was cancelled")))
            .ToList();

        var refusal = failures.FindIndex(failure => failure.Exception is HarnessException);
        var raised = failures[refusal < 0 ? 0 : refusal];

        foreach (var failure in failures.Where(failure => !ReferenceEquals(failure.Exception, raised.Exception)))
        {
            _output.Warn(CommandName, $"{failure.Host} could not be measured: {failure.Exception.Message}");
        }

        ExceptionDispatchInfo.Capture(raised.Exception).Throw();
    }

    /// <summary>The emulators the selected legs use: the only ones worth running a witness for.</summary>
    private static Dictionary<string, EmulatorConfig> EmulatorsUsedBy(HarnessConfig config, LegSelection selection)
        => selection.Legs
            .Select(selected => selected.Leg.Emulator)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, name => config.Emulators[name], StringComparer.OrdinalIgnoreCase);
}
