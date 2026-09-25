using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Runs;

/// <summary>One placed leg, with everything decided before anything starts.</summary>
/// <param name="Name">The leg's name.</param>
/// <param name="Leg">What it declares.</param>
/// <param name="Host">The host it runs on.</param>
/// <param name="Project">The project it builds, or null when it builds nothing.</param>
/// <param name="Variant">What makes its build different from every other leg's.</param>
/// <param name="TreeRoot">
/// The tree it acts on <em>here</em>: the main checkout or a worktree. This is what a sync reads,
/// and for a leg that landed on another machine it is never where the work happens.
/// </param>
/// <param name="HostTreeRoot">
/// The tree the work happens in, on the machine it happens on: the same as <paramref name="TreeRoot"/>
/// for a local leg, and the host's own copy for every other. Every path that describes the work
/// derives from this one, because a path derived from <paramref name="TreeRoot"/> for a remote leg
/// names a directory on the machine that typed the command rather than on the one being measured.
/// </param>
/// <param name="BuildDirectory">Its variant-keyed build directory below <paramref name="HostTreeRoot"/>.</param>
/// <param name="HostSettings">
/// What the host it landed on declares for itself: its core counts, what it keeps awake, where its
/// compiler cache lives. Resolved with the leg rather than looked up at each use, so a machine that
/// declares four cores is not built on with the count of the machine that typed the command.
/// </param>
/// <param name="Emulated">Whether it runs through an emulator, so its timings are never compared with a native leg's.</param>
public sealed record PlacedLeg(
    string Name,
    LegConfig Leg,
    HostReport Host,
    ProjectConfig? Project,
    VariantKey Variant,
    string TreeRoot,
    string HostTreeRoot,
    string BuildDirectory,
    HostSettings HostSettings,
    bool Emulated)
{
    /// <summary>The host and tree this leg shares a sync with, so a tree is synced once, not once per leg.</summary>
    /// <remarks>
    /// Keyed by the tree on the host, never by the tree here. Each tree has a copy of its own on a
    /// host, but two worktrees kept under one name would write into one: keyed by their sources they
    /// would look like two trees, be synced twice one over the other, and the leg that lost would
    /// build sources the other was halfway through replacing.
    /// </remarks>
    public string TreeKey => CompositeKey.Of(Host.Host.ToString(), HostTreeRoot);

    /// <summary>
    /// The host as the machine that typed the command names it: the one this leg landed on, or, where
    /// a host runs a leg another machine dispatched to it, the name that machine knows it by.
    /// </summary>
    /// <remarks>
    /// What the leg is called wherever a reader sees its host - a <c>{host}</c> a label records, the
    /// progress a dispatching machine shows - and what its settings were read under. Never what it
    /// is locked or scheduled by: that is the machine the work physically runs on, which to itself
    /// is always this one.
    /// </remarks>
    public HostId Named
    {
        get => _named ?? Host.Host;
        init => _named = value;
    }

    private readonly HostId? _named;

    /// <summary>
    /// What the developer environment the leg's toolchain names set up for it, on the machine that runs
    /// it: empty until it is set up there, and for a leg whose toolchain names none.
    /// </summary>
    public IReadOnlyDictionary<string, string> DeveloperEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The environment every process the leg starts is given beneath its command's own: what its host
    /// declares under <c>env</c>, with its <see cref="DeveloperEnvironment"/> over it.
    /// </summary>
    /// <remarks>
    /// Over the host's rather than beneath it, because it was set up under the host's: a PATH it put
    /// Visual Studio's directories ahead of already holds the one the host declares, and ranked beneath
    /// it that PATH would lose them.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Environment
        => PhaseEnvironment.Layered(HostSettings.Env, DeveloperEnvironment)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);

    /// <summary>The build this leg runs, with what its host declares for it.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="runDirectory">Where this run's logs go.</param>
    /// <param name="time">Whether to report the profile timing.</param>
    /// <remarks>
    /// Made here once, for every command that builds a leg - a build, a test that builds first, a run
    /// whose runner needs the compiler - so what the host declares reaches every one of them or none.
    /// </remarks>
    /// <exception cref="HarnessException">The leg builds nothing; see <see cref="BuildableProject"/>.</exception>
    public BuildRequest BuildRequestFor(HarnessConfig config, string runDirectory, bool time = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new BuildRequest(
            Name,
            TreeRoot,
            BuildableProject(),
            Variant,
            Host.Os ?? string.Empty,
            CoreCounts.Resolve(null, HostSettings.BuildCores, config.Defaults.BuildCores).Value,
            runDirectory,
            time)
        {
            ProgramDirectories = Host.ProgramDirectories,
            HostEnvironment = Environment,
        };
    }

    /// <summary>
    /// The project this leg builds, or a refusal naming what is missing.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The leg names no project, or no toolchain for the host it landed on. Asked only by the
    /// commands that build, so a leg that only runs a predefined runner never has to declare either.
    /// </exception>
    public ProjectConfig BuildableProject()
    {
        if (Project is null)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Leg '{Name}' builds nothing: it names no project, and neither defaults.project nor a "
                + "single declared project supplies one.");
        }

        return Variant.Buildable
            ? Project
            : throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Leg '{Name}' names no toolchain, and project '{Project.Name}' declares no default "
                + $"toolchain for {Host.Os}.");
    }

    /// <summary>
    /// Who this leg is, in the words a configured command can spell.
    /// </summary>
    /// <param name="runId">The run this work belongs to.</param>
    /// <remarks>
    /// Derived here and nowhere else, so a benchmark's label and a build directory's name cannot
    /// come to disagree about which processor a leg targets. The processor is the leg's own, not the
    /// host's: under emulation those differ, and what a measurement was taken *of* is the target.
    /// </remarks>
    public Execution.LegIdentity IdentityFor(string runId) => new(
        Leg: Name,
        Os: Host.Os ?? Leg.Os,
        Processor: Variant.Processor,
        Toolchain: Variant.Toolchain,
        Config: Variant.Config,
        Variant: Variant.DirectoryName,
        Host: Named.ToString(),
        RunId: runId);

    /// <summary>
    /// The one file this leg's build is declared to produce, and why there is none when there is
    /// not.
    /// </summary>
    /// <param name="buildDirectory">
    /// The build directory the caller is handing this same work, so the product and
    /// <c>{buildDir}</c> cannot come from two different tree roots. They do differ: a leg placed on
    /// another machine has a host tree root that is not this machine's, and a run re-invoked there
    /// resolves its own.
    /// </param>
    /// <remarks>
    /// A single answer or none. <c>buildOutputs</c> is a list, every entry of which must exist for a
    /// build to be witnessed, so "the product" is a well-formed question only where the list holds
    /// exactly one path for this platform. Where it holds several, this returns the reason instead
    /// of a guess: an instrument pointed at the wrong one of three binaries measures something
    /// nobody asked about and reports it as a success.
    /// </remarks>
    public (string? Path, string? Problem) ProductFor(string buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        if (Project is null)
        {
            return (null, $"leg '{Name}' builds nothing, so it has no product");
        }

        var platform = Host.Os ?? Leg.Os;

        var declared = Project.BuildOutputs
            .Select(output => output.For(platform))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        return declared.Count switch
        {
            1 => (Path.Combine(buildDirectory, declared[0]), null),
            0 => (null, $"project '{Project.Name}' declares no buildOutputs for {platform}"),
            _ => (
                null,
                $"project '{Project.Name}' declares {declared.Count} buildOutputs for {platform} "
                + $"({string.Join(", ", declared)}), so which one is 'the product' is not something "
                + "this tool can decide"),
        };
    }

    /// <summary>What the executor needs to schedule this leg.</summary>
    /// <remarks>
    /// A local leg carries no tree key, because it needs no sync: the executor announces a transfer
    /// for every leg that has one, and a leg working in the tree the command was typed in would
    /// otherwise report a transfer that never happened.
    /// </remarks>
    public LegPlan ToPlan() => new()
    {
        Name = Name,
        BuildDirectory = BuildDirectory,
        TreeKey = Host.Host.Kind == HostKind.Local ? string.Empty : TreeKey,
        Emulated = Emulated,
        MachineKey = Host.Host.MachineKey,
        Host = Named.ToString(),
    };
}

/// <summary>
/// Turns a leg selection and the hosts that were measured into placed legs, refusing before anything
/// starts what cannot run.
/// </summary>
public static class LegRunPlan
{
    /// <summary>
    /// Builds the placed legs for a report, with the legs no host can run recorded as skipped.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="report">Where each selected leg can run.</param>
    /// <param name="platform">Supplies how paths compare on this machine.</param>
    /// <param name="skipped">Receives one ledger entry per leg that cannot run.</param>
    /// <exception cref="HarnessException">
    /// Two selected legs resolve to the same build directory. Refused before either starts, because
    /// by the time the second reached its build directory the first would already have been
    /// reconfigured out from under itself, and both would report on objects neither alone produced.
    /// </exception>
    public static IReadOnlyList<PlacedLeg> From(
        HarnessContext context,
        LegsReport report,
        IHostPlatform platform,
        out IReadOnlyList<LegEntry> skipped)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(platform);

        var placed = new List<PlacedLeg>();
        var notRun = new List<LegEntry>();

        foreach (var placement in report.Placements)
        {
            if (placement.Host is null || !placement.Runnable)
            {
                notRun.Add(new LegEntry
                {
                    Leg = placement.Leg.Name,
                    Verdict = placement.Verdict,
                    Detail = placement.Reason ?? "no host can run it",
                });

                continue;
            }

            placed.Add(Place(context, placement.Leg, placement.Host, report.Here, platform.PathComparison));
        }

        RefuseSharedBuildDirectories(placed, platform);
        RefuseSharedHostCopies(placed, platform);

        skipped = notRun;
        return placed;
    }

    /// <summary>
    /// What a command reports when no selected leg can run: why each could not, under the verdict the
    /// run would have recorded for it.
    /// </summary>
    /// <param name="skipped">The line each leg would have had.</param>
    /// <remarks>
    /// A leg turned away by a defect in this tool keeps that defect's exit code even though nothing
    /// ran: "no selected leg can run" and nothing else would send the reader to the hosts.
    /// </remarks>
    public static CommandOutcome NothingRuns(IReadOnlyList<LegEntry> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        var worst = Verdicts.Describe(Verdicts.Worst(skipped.Select(entry => entry.Verdict)));

        return CommandOutcome.Failed(
            worst.IsFailure ? worst.ExitCode : LegsExit.Unavailable,
            "no selected leg can run",
            [.. skipped.Select(entry => $"{entry.Leg}: {Verdicts.Display(entry.Verdict)}: {entry.Detail}")]);
    }

    private static PlacedLeg Place(HarnessContext context, SelectedLeg selected, HostReport host, HostId? here, StringComparison comparison)
    {
        var config = context.Config;
        var leg = selected.Leg;

        // Neither is required here. A leg that only runs a predefined runner compiles nothing, and
        // making it declare a project it has no use for would be a demand the tool invents. The
        // commands that do build refuse a leg with no project, naming what is missing.
        var project = VariantKey.ProjectFor(config, leg);
        var variant = VariantKey.For(config, leg, host.Os ?? string.Empty);

        var treeRoot = LegTrees.Here(context, leg, here);

        // Where the work actually happens. A leg on another machine works in that machine's copy of its
        // tree, and describing it with a path from this one would key its lock, its sync and its build
        // directory by a directory the work never touches. Resolved here rather than at the moment of the
        // sync, so a host that declares no repositoryPath is refused before anything starts.
        var hostTreeRoot = LegTrees.On(context, host.Host, treeRoot, comparison);

        // Read under the name the reader knows the host by. A host running a leg another machine
        // dispatched to it is 'local' to itself, and 'local' in the configuration the two share is
        // the machine that dispatched it: that machine's cores and environment were the ones the leg
        // ran with.
        var named = here ?? host.Host;

        return new PlacedLeg(
            selected.Name,
            leg,
            host,
            project,
            variant,
            treeRoot,
            hostTreeRoot,
            variant.DirectoryOn(host.Host, hostTreeRoot),
            config.Hosts.SettingsFor(named),
            leg.Emulator is { Length: > 0 })
        {
            Named = named,
        };
    }

    /// <summary>
    /// Refuses two legs that would carry different trees into one copy on one host.
    /// </summary>
    /// <remarks>
    /// Each tree has a copy of its own on a host, kept under its directory's name, so two trees share
    /// one only where two worktrees are kept under one name - two directories called 'feature', say.
    /// Both would sync into it, the second over the first, and whichever leg ran second would measure
    /// a tree the other one put there. Caught here because after the sync there is nothing left to
    /// notice: both legs find exactly the tree they asked for, one of them just finds it some time
    /// after it stopped being true.
    /// </remarks>
    private static void RefuseSharedHostCopies(List<PlacedLeg> placed, IHostPlatform platform)
    {
        var comparer = platform.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        var contested = placed
            .Where(leg => leg.Host.Host.Kind != HostKind.Local)
            .GroupBy(leg => leg.TreeKey, LegPlan.TreeKeyComparer)
            .Where(group => group.Select(leg => leg.TreeRoot).Distinct(comparer).Count() > 1)
            .Select(group =>
                $"{string.Join(" and ", group.Select(leg => $"'{leg.Name}'"))} would each put a different tree in "
                + $"'{group.First().HostTreeRoot}' on {group.First().Named}: "
                + string.Join(", ", group.Select(leg => leg.TreeRoot).Distinct(comparer)))
            .ToList();

        if (contested.Count == 0)
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.Refused,
            $"Selected legs disagree about what a host's copy should hold: {string.Join("; ", contested)}. "
            + "Nothing was run. Rename one of the worktrees, or run them separately.");
    }

    /// <summary>
    /// Refuses two legs that would build in one directory, before either starts.
    /// </summary>
    /// <remarks>
    /// Caught here rather than by the build directory guard, which would only notice once the second
    /// leg reached it — by which time the first has already been reconfigured out from under itself,
    /// and both legs report on objects neither of them alone produced.
    /// </remarks>
    private static void RefuseSharedBuildDirectories(List<PlacedLeg> placed, IHostPlatform platform)
    {
        var comparer = platform.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        var byDirectory = placed
            .GroupBy(
                leg => CompositeKey.Of(leg.Host.Host.ToString(), Path.TrimEndingDirectorySeparator(leg.BuildDirectory)),
                comparer)
            .Where(group => group.Count() > 1)
            .ToList();

        if (byDirectory.Count == 0)
        {
            return;
        }

        var described = byDirectory.Select(group =>
            $"{string.Join(" and ", group.Select(leg => $"'{leg.Name}'"))} both build in "
            + $"'{group.First().BuildDirectory}' on {group.First().Named}");

        throw new HarnessException(
            HarnessExit.Refused,
            $"Selected legs share a build directory: {string.Join("; ", described)}. Nothing was run. "
            + "Give them different processors, toolchains, build configurations or sanitizers, or "
            + "select one of each pair.");
    }
}
