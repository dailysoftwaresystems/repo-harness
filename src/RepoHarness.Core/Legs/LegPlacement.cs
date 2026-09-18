using RepoHarness.Core.Configuration;
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

    /// <summary>
    /// Whether a host that was right for the leg in every other way turned it away only because a
    /// program its build or test starts is not there.
    /// </summary>
    /// <remarks>
    /// Kept apart from the reason's wording so a run can report such a leg as skipped for a missing
    /// tool rather than as skipped for want of a host. The two send somebody to different places:
    /// one to install a program, the other to switch a machine on.
    /// </remarks>
    public bool ToolMissing { get; init; }

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
    /// <param name="reports">What measurement found, by host.</param>
    /// <param name="here">Whether this machine is the only candidate, whatever the leg names.</param>
    public static LegPlacement Place(
        HarnessConfig config,
        SelectedLeg selected,
        IReadOnlyDictionary<HostId, HostReport> reports,
        bool here = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(reports);

        var reasons = new List<string>();
        var toolMissing = false;

        foreach (var candidate in Candidates(config, selected.Leg, here))
        {
            if (!reports.TryGetValue(candidate, out var report))
            {
                continue;
            }

            if (PlatformObstacle(selected.Leg, report) is { } platform)
            {
                reasons.Add($"{candidate}: {platform}");
                continue;
            }

            if (MissingPrograms(config, selected.Leg, report) is { } missing)
            {
                reasons.Add($"{candidate}: {missing}");
                toolMissing = true;
                continue;
            }

            return new LegPlacement(selected, report, null);
        }

        return new LegPlacement(selected, null, reasons.Count == 0 ? "no host was measured for it" : string.Join("; ", reasons))
        {
            ToolMissing = toolMissing,
        };
    }

    /// <summary>What stops <paramref name="host"/> from running <paramref name="leg"/>, or <see langword="null"/> when nothing does.</summary>
    /// <param name="config">The whole configuration, which says what the leg's build and test start.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="host">What measuring the host found.</param>
    public static string? Obstacle(HarnessConfig config, LegConfig leg, HostReport host)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(host);

        return PlatformObstacle(leg, host) ?? MissingPrograms(config, leg, host);
    }

    /// <summary>
    /// What stops <paramref name="host"/> from running <paramref name="leg"/> before its programs
    /// are asked about: it is unreachable, or the wrong machine.
    /// </summary>
    /// <remarks>
    /// Asked first, because a host that is the wrong machine should say so. Told instead that cmake
    /// is missing on a Windows host being considered for a Linux leg, a reader would go and install
    /// cmake on a machine the leg will never run on.
    /// </remarks>
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
    /// The programs <paramref name="leg"/>'s build and test start that <paramref name="host"/> does
    /// not have, said as one reason, or <see langword="null"/> when it has them all.
    /// </summary>
    /// <remarks>
    /// Read from what the host itself found, with the search a leg there will use. A program the host
    /// was not asked about is reported as that rather than as missing: a survey that had not looked
    /// must not read as one that looked and found nothing, nor as one that found it.
    /// </remarks>
    private static string? MissingPrograms(HarnessConfig config, LegConfig leg, HostReport host)
    {
        var missing = LegPrograms.For(config, leg)
            .Where(program => !host.Programs.TryGetValue(program, out var location) || !location.Present)
            .ToList();

        if (missing.Count == 0)
        {
            return null;
        }

        var unasked = missing.Where(program => !host.Programs.ContainsKey(program)).ToList();
        var absent = missing.Except(unasked, StringComparer.Ordinal).ToList();
        var said = new List<string>();

        if (absent.Count > 0)
        {
            said.Add(
                $"{Quoted(absent)} {(absent.Count == 1 ? "is" : "are")} not installed there: neither on the PATH a "
                + "command run there sees nor in any directory searched for programs; install "
                + $"{(absent.Count == 1 ? "it" : "them")}, or name the directory under toolSearchDirectories");
        }

        if (unasked.Count > 0)
        {
            said.Add($"whether {Quoted(unasked)} {(unasked.Count == 1 ? "is" : "are")} there was not asked");
        }

        return string.Join("; ", said);
    }

    private static string Quoted(IEnumerable<string> programs) => string.Join(", ", programs.Select(program => $"'{program}'"));

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
