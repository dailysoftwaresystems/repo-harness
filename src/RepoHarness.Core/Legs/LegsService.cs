using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;

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
/// them, bringing repo-harness on each to this machine's build, and places each leg on the first host
/// that provides its operating system, its processor and its emulator.
/// </summary>
public sealed class LegsService(IHarnessContextLoader contextLoader, IHostInspector inspector, IHarnessOutput output)
{
    /// <summary>The command's name, which prefixes what it reports.</summary>
    public const string CommandName = "legs";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostInspector _inspector = inspector;
    private readonly IHarnessOutput _output = output;

    /// <summary>Checks the legs <paramref name="legNames"/> selects, or every leg when it is empty.</summary>
    public async Task<LegsReport> CheckAsync(
        string directory,
        IReadOnlyList<string> legNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(legNames);

        var context = await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
        var config = context.Config;
        var selection = LegSelection.Resolve(config, legNames);
        var emulators = EmulatorsUsedBy(config, selection);
        var candidates = selection.Legs.Select(leg => (leg, Hosts: LegPlacement.Candidates(config, leg.Leg))).ToList();
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

        var measured = await Task.WhenAll(
            remote.Select(host => _inspector.InspectAsync(context, host, emulators, cancellationToken))).ConfigureAwait(false);

        foreach (var report in measured)
        {
            reports[report.Host] = report;
        }

        var placements = selection.Legs.Select(leg => LegPlacement.Place(config, leg, reports)).ToList();

        // Each leg that cannot run is its own warning, naming it and saying why, while the others go on.
        foreach (var placement in placements.Where(placement => !placement.Runnable))
        {
            _output.Warn(CommandName, $"leg '{placement.Leg.Name}' cannot run: {placement.Reason}");
        }

        return new LegsReport(placements, [.. reports.Values], selection.Named);
    }

    /// <summary>The emulators the selected legs use: the only ones worth running a witness for.</summary>
    private static Dictionary<string, EmulatorConfig> EmulatorsUsedBy(HarnessConfig config, LegSelection selection)
        => selection.Legs
            .Select(selected => selected.Leg.Emulator)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, name => config.Emulators[name], StringComparer.OrdinalIgnoreCase);
}
