using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Legs;

/// <summary>Where a leg runs, or why no host can run it.</summary>
/// <param name="Leg">The leg.</param>
/// <param name="Host">The host that runs it, or <see langword="null"/> when none can.</param>
/// <param name="Reason">Why no host can run it, when none can.</param>
public sealed record LegPlacement(SelectedLeg Leg, HostReport? Host, string? Reason)
{
    /// <summary>Whether a host can run the leg.</summary>
    public bool Runnable => Host is not null;

    /// <summary>What a run records for the leg when no host can run it.</summary>
    /// <remarks>
    /// Decided with the reason rather than read back out of its wording, because each sends somebody
    /// somewhere different: skipped for a missing tool, to install a program on a host that is right
    /// in every other way; skipped as unavailable, to switch a machine on or reach it; and poisoned,
    /// to report a defect in this tool - a host never asked about a program the leg starts.
    /// </remarks>
    public LegVerdict Verdict { get; init; } = LegVerdict.SkippedUnavailable;

    /// <summary>
    /// The hosts that may run <paramref name="leg"/>, in the order they are tried: the one host it names,
    /// or else this machine, then the WSL distributions when the leg runs on Linux, then the ssh hosts, each
    /// in the order the configuration declares them, and each named as the configuration declares it.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg being placed.</param>
    /// <param name="here">
    /// Whether this machine is the only candidate, whatever the leg names. Set when a host is
    /// running a leg the machine that reached it dispatched: the leg names that host, and asking it
    /// to place the leg again would send it looking for connection data it was deliberately never
    /// given, to reach a machine it already is.
    /// </param>
    public static IReadOnlyList<HostId> Candidates(HarnessConfig config, LegConfig leg, bool here = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);

        if (here)
        {
            return [HostId.Local];
        }

        if (leg.Wsl is { } wsl)
        {
            return [HostId.Wsl(DeclaredName.In(config.Hosts.Wsl.Keys, wsl) ?? wsl)];
        }

        if (leg.Ssh is { } ssh)
        {
            return [HostId.Ssh(DeclaredName.In(config.Hosts.Ssh.Keys, ssh) ?? ssh)];
        }

        // A WSL distribution runs Linux and nothing else. Measuring one for a leg on another operating system
        // would install DssHarness there for a leg it can never run, or stop the check over its version.
        var distributions = Same(leg.Os, PlatformNames.Linux)
            ? config.Hosts.Wsl.Keys.Select(HostId.Wsl)
            : Enumerable.Empty<HostId>();

        return [HostId.Local, .. distributions, .. config.Hosts.Ssh.Keys.Select(HostId.Ssh)];
    }

    /// <summary>
    /// Places a leg on the first of its candidates that measurement shows can run it. A candidate
    /// missing from <paramref name="reports"/> was not measured, and is passed over.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="selected">The leg being placed.</param>
    /// <param name="workload">What the command has the leg do, which says what its host must have.</param>
    /// <param name="reports">What measurement found, by host.</param>
    /// <param name="here">Whether this machine is the only candidate, whatever the leg names.</param>
    public static LegPlacement Place(
        HarnessConfig config,
        SelectedLeg selected,
        LegWorkload workload,
        IReadOnlyDictionary<HostId, HostReport> reports,
        bool here = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(reports);

        var programs = LegPrograms.For(config, selected.Leg, workload);
        var reasons = new List<string>();
        var verdict = LegVerdict.SkippedUnavailable;

        foreach (var candidate in Candidates(config, selected.Leg, here))
        {
            if (!reports.TryGetValue(candidate, out var report))
            {
                continue;
            }

            if (Refusal(selected.Leg, programs, report) is not { } refusal)
            {
                return new LegPlacement(selected, report, null);
            }

            reasons.Add($"{candidate}: {refusal.Reason}");
            verdict = Graver(verdict, refusal.Verdict);
        }

        return new LegPlacement(selected, null, reasons.Count == 0 ? "no host was measured for it" : string.Join("; ", reasons))
        {
            Verdict = verdict,
        };
    }

    /// <summary>What stops <paramref name="host"/> from running <paramref name="leg"/>, or <see langword="null"/> when nothing does.</summary>
    /// <param name="config">The whole configuration, which says what the leg's build and test start.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="workload">What the command has the leg do.</param>
    /// <param name="host">What measuring the host found.</param>
    public static string? Obstacle(HarnessConfig config, LegConfig leg, LegWorkload workload, HostReport host)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(host);

        return Refusal(leg, LegPrograms.For(config, leg, workload), host)?.Reason;
    }

    /// <summary>
    /// Why <paramref name="host"/> turns <paramref name="leg"/> away, and what a run records for
    /// that, or <see langword="null"/> when it takes the leg. The one decision placing a leg and
    /// asking about one host both read, so the two cannot come to disagree about a host.
    /// </summary>
    /// <remarks>
    /// The machine is asked about before its programs, because a host that is the wrong machine
    /// should say so. Told instead that cmake is missing on a Windows host being considered for a
    /// Linux leg, a reader would go and install cmake on a machine the leg will never run on.
    /// </remarks>
    private static (string Reason, LegVerdict Verdict)? Refusal(LegConfig leg, IReadOnlyList<string> programs, HostReport host)
        => PlatformObstacle(leg, host) is { } platform
            ? (platform, LegVerdict.SkippedUnavailable)
            : MissingPrograms(programs, host);

    /// <summary>
    /// The graver of two verdicts for a leg every candidate turned away: a defect in this tool over
    /// anything, then a program a right host lacks - which somebody can install - over a host that
    /// could not take the leg at all.
    /// </summary>
    private static LegVerdict Graver(LegVerdict first, LegVerdict second)
        => Gravity(second) > Gravity(first) ? second : first;

    private static int Gravity(LegVerdict verdict) => verdict switch
    {
        LegVerdict.Poisoned => 2,
        LegVerdict.SkippedToolMissing => 1,
        _ => 0,
    };

    /// <summary>
    /// What stops <paramref name="host"/> from running <paramref name="leg"/> before its programs
    /// are asked about: it is unreachable, or the wrong machine.
    /// </summary>
    private static string? PlatformObstacle(LegConfig leg, HostReport host)
    {
        if (!host.Available)
        {
            return host.Reason;
        }

        if (!Same(host.Os, leg.Os))
        {
            return $"it runs {host.Os}, and the leg needs {leg.Os}";
        }

        if (leg.Emulator is null)
        {
            return Same(host.Processor, leg.Processor)
                ? null
                : $"it is {host.Processor}, and the leg runs natively on {leg.Processor}";
        }

        if (!host.Emulators.TryGetValue(leg.Emulator, out var check))
        {
            return $"emulator '{leg.Emulator}' was not checked there";
        }

        return check.Available ? null : $"emulator '{leg.Emulator}' cannot run there: {check.Reason}";
    }

    /// <summary>
    /// The programs of <paramref name="programs"/> that <paramref name="host"/> is not known to have,
    /// said as one reason with what a run records for it, or <see langword="null"/> when it has them all.
    /// </summary>
    /// <remarks>
    /// Read from what the host itself found, with the search a leg there will use. Three different
    /// facts, kept apart: a program the search looked for everywhere and did not find is missing; one
    /// it could not look for everywhere is not known either way, and no refusal may call it missing;
    /// and one the host was never asked about is a defect in this tool, since a host is asked about
    /// every program any leg starts. A survey that had not looked must not read as one that looked
    /// and found nothing, nor as one that found it.
    /// </remarks>
    private static (string Reason, LegVerdict Verdict)? MissingPrograms(IReadOnlyList<string> programs, HostReport host)
    {
        var absent = new List<string>();
        var unknown = new List<string>();
        var unasked = new List<string>();

        foreach (var program in programs)
        {
            switch (host.Programs.GetValueOrDefault(program)?.Found)
            {
                case ProgramFound.OnPath or ProgramFound.OffPath:
                    break;
                case ProgramFound.Nowhere:
                    absent.Add(program);
                    break;
                case ProgramFound.Unreadable:
                    unknown.Add(program);
                    break;
                default:
                    unasked.Add(program);
                    break;
            }
        }

        var said = new List<string>();

        if (absent.Count > 0)
        {
            said.Add(
                $"{Quoted(absent)} {(absent.Count == 1 ? "is" : "are")} not installed there: neither on the PATH a "
                + "command run there sees nor in any directory searched for programs; install "
                + $"{(absent.Count == 1 ? "it" : "them")}, or name the directory under toolSearchDirectories");
        }

        foreach (var program in unknown)
        {
            said.Add($"whether '{program}' is there could not be established: {host.Programs[program].Reason ?? "the host did not say why"}");
        }

        if (unasked.Count > 0)
        {
            said.Add(
                $"whether {Quoted(unasked)} {(unasked.Count == 1 ? "is" : "are")} there was never asked, which is a "
                + "defect in this tool: a host is asked about every program a leg starts");
        }

        if (said.Count == 0)
        {
            return null;
        }

        var verdict = unasked.Count > 0 ? LegVerdict.Poisoned
            : absent.Count > 0 ? LegVerdict.SkippedToolMissing
            : LegVerdict.SkippedUnavailable;

        return (string.Join("; ", said), verdict);
    }

    private static string Quoted(IEnumerable<string> programs) => string.Join(", ", programs.Select(program => $"'{program}'"));

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
