using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Legs;

/// <summary>Where a leg runs, or why it does not: no host can take it, or the one it went to lacks what it needs.</summary>
/// <param name="Leg">The leg.</param>
/// <param name="Host">The host that runs it, or <see langword="null"/> when it does not run.</param>
/// <param name="Reason">Why it does not run, when it does not.</param>
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
    /// Places a leg on the first of its candidates that can take it - the right machine, reached,
    /// with its emulator working - and turns it away there when that host lacks what
    /// <paramref name="workload"/> starts. A candidate missing from <paramref name="reports"/> was not
    /// measured, and is passed over.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="selected">The leg being placed.</param>
    /// <param name="workload">What the command has the leg do, which says what its host must have.</param>
    /// <param name="reports">What measurement found, by host.</param>
    /// <param name="here">Whether this machine is the only candidate, whatever the leg names.</param>
    /// <remarks>
    /// A program never chooses the host. Chosen by what each command starts, a leg went to one machine
    /// for its build and another for its tests, and a run on what a sync had staged went to a host the
    /// sync never put the tree on - while a leg quietly measured a different machine from the one it
    /// ran on the day before, because somebody had installed a tool there. Every command places a leg
    /// where a copy of its tree goes, and a program that host lacks is said to be missing there. To
    /// run a leg on another host, the leg names that host.
    /// <para>
    /// The machine is asked about before its programs, because a host that is the wrong machine
    /// should say so. Told instead that cmake is missing on a Windows host being considered for a
    /// Linux leg, a reader would go and install cmake on a machine the leg will never run on.
    /// </para>
    /// <para>
    /// A host running a leg for the machine that dispatched it - <paramref name="here"/> - names
    /// no candidate in its reasons: it is the only one, and the name it has for itself is not the
    /// name the machine that reads the reason knows it by.
    /// </para>
    /// </remarks>
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

        var reasons = new List<string>();

        foreach (var candidate in Candidates(config, selected.Leg, here))
        {
            if (!reports.TryGetValue(candidate, out var report))
            {
                continue;
            }

            var at = here ? string.Empty : $"{candidate}: ";

            if (PlatformObstacle(selected.Leg, report) is { } passedOver)
            {
                reasons.Add(at + passedOver);
                continue;
            }

            return MissingPrograms(LegPrograms.For(config, selected.Leg, workload), report) is { } refused
                ? new LegPlacement(selected, null, at + refused.Reason) { Verdict = refused.Verdict }
                : new LegPlacement(selected, report, null);
        }

        return new LegPlacement(selected, null, reasons.Count == 0 ? "no host was measured for it" : string.Join("; ", reasons));
    }

    /// <summary>
    /// What stops <paramref name="host"/> from taking <paramref name="leg"/> at all, before its
    /// programs are asked about: it is unreachable, the wrong machine, or its emulator does not work
    /// there. <see langword="null"/> when the host can take it: the one test that decides which host
    /// a leg goes to, and which hosts are measured for it.
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="host">What measuring the host found.</param>
    public static string? PlatformObstacle(LegConfig leg, HostReport host)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(host);

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
        var nothingAt = new List<string>();
        var unknown = new List<string>();
        var unasked = new List<string>();

        foreach (var program in programs)
        {
            switch (host.Programs.GetValueOrDefault(program)?.Found)
            {
                case ProgramFound.OnPath or ProgramFound.OffPath:
                    break;
                case ProgramFound.Nowhere:
                    (ProcessRunner.IsPath(program) ? nothingAt : absent).Add(program);
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

        // A program named by its path is looked for at that path and nowhere else, so no directory
        // searched for programs has anything to do with it.
        if (nothingAt.Count > 0)
        {
            said.Add(
                $"nothing is at {Quoted(nothingAt)} there; install {(nothingAt.Count == 1 ? "it" : "them")} "
                + "at that path, or name the program where it is");
        }

        foreach (var program in unknown)
        {
            said.Add(host.Programs[program].Unestablished());
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
            : absent.Count + nothingAt.Count > 0 ? LegVerdict.SkippedToolMissing
            : LegVerdict.SkippedUnavailable;

        return (string.Join("; ", said), verdict);
    }

    private static string Quoted(IEnumerable<string> programs) => string.Join(", ", programs.Select(program => $"'{program}'"));

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
