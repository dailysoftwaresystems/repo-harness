using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;

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
    /// The hosts that may run <paramref name="leg"/>, in the order they are tried: the one host it names,
    /// or else this machine, then the WSL distributions, then the ssh hosts, each in the order the
    /// configuration declares them.
    /// </summary>
    public static IReadOnlyList<HostId> Candidates(HarnessConfig config, LegConfig leg)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);

        if (leg.Wsl is { } wsl)
        {
            return [HostId.Wsl(wsl)];
        }

        if (leg.Ssh is { } ssh)
        {
            return [HostId.Ssh(ssh)];
        }

        return [HostId.Local, .. config.Hosts.Wsl.Keys.Select(HostId.Wsl), .. config.Hosts.Ssh.Keys.Select(HostId.Ssh)];
    }

    /// <summary>
    /// Places a leg on the first of its candidates that measurement shows can run it. A candidate
    /// missing from <paramref name="reports"/> was not measured, and is passed over.
    /// </summary>
    public static LegPlacement Place(
        HarnessConfig config,
        SelectedLeg selected,
        IReadOnlyDictionary<HostId, HostReport> reports)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(reports);

        var reasons = new List<string>();

        foreach (var candidate in Candidates(config, selected.Leg))
        {
            if (!reports.TryGetValue(candidate, out var report))
            {
                continue;
            }

            var obstacle = Obstacle(selected.Leg, report);

            if (obstacle is null)
            {
                return new LegPlacement(selected, report, null);
            }

            reasons.Add($"{candidate}: {obstacle}");
        }

        return new LegPlacement(selected, null, reasons.Count == 0 ? "no host was measured for it" : string.Join("; ", reasons));
    }

    /// <summary>What stops <paramref name="host"/> from running <paramref name="leg"/>, or <see langword="null"/> when nothing does.</summary>
    public static string? Obstacle(LegConfig leg, HostReport host)
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

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
