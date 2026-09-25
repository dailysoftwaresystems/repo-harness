using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Legs;

/// <summary>Turns what checking legs found into what <c>legs</c> prints.</summary>
/// <remarks>
/// JSON is written alone on standard output, with each leg that cannot run warned about on standard
/// error, so the document always parses.
/// </remarks>
public static class LegsReports
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What <c>legs</c> reports.</summary>
    public static CommandOutcome Render(LegsReport report, bool json, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        // A leg no host can run is a complete answer: the survey looked and says so. A host that
        // could not be reached is not — nothing was established about the legs it would have taken,
        // and 'OK' for both differs only by a number somebody has to parse out of prose. Incomplete
        // is the code the run verbs already use for exactly this: nothing failed, and not
        // everything reported.
        var silent = report.Hosts.Where(host => !host.Available).ToList();

        var exitCode = report.Defect is not null
            ? Verdicts.ExitCodeFor(LegVerdict.Poisoned)
            : !report.Passed
                ? LegsExit.Unavailable
                : silent.Count > 0
                    ? HarnessExit.Incomplete
                    : HarnessExit.Success;

        var message = Summary(report, silent);

        if (json)
        {
            var document = new
            {
                Legs = report.Placements.Select(placement => new
                {
                    placement.Leg.Name,
                    placement.Leg.Leg.Os,
                    placement.Leg.Leg.Processor,
                    placement.Leg.Leg.Emulator,
                    placement.Runnable,
                    Host = placement.Host?.Host.ToString(),
                    placement.Reason,
                }),
                Hosts = report.Hosts.Select(host => new
                {
                    Host = host.Host.ToString(),
                    host.Available,
                    host.Reason,
                    host.Os,
                    host.Processor,
                    host.ToolVersion,
                    host.ToolPath,
                    host.Actions,

                    // The room where its copies are kept - the main checkout, for this machine -
                    // or why it could not be measured: whole here, where -v adds nothing a script has to ask for.
                    host.Space,
                    host.SpaceUnmeasured,
                }),
            };

            return new CommandOutcome(exitCode, message)
            {
                Data = [JsonSerializer.Serialize(document, JsonOptions)],
                Quiet = true,
            };
        }

        var details = new List<string>();

        foreach (var host in report.Hosts)
        {
            details.AddRange(host.Actions.Select(action => $"{host.Host}: {action}"));

            // A leg placed on a host further down its candidates says nothing of the hosts it passed over.
            if (host.Reason is { } reason)
            {
                details.Add($"{host.Host}: cannot run legs: {reason}");
            }

            // Asked for with -v: a host that is nearly full shows before a run that fills it, and before the
            // legs its builds would no longer fit are turned away.
            if (verbose && Room(host) is { } room)
            {
                details.Add($"{host.Host}: {room}");
            }
        }

        var width = report.Placements.Count == 0 ? 0 : report.Placements.Max(placement => placement.Leg.Name.Length);

        foreach (var placement in report.Placements.Where(placement => placement.Runnable))
        {
            var host = placement.Host!;
            var through = placement.Leg.Leg.Emulator is { } emulator ? $" through {emulator}" : string.Empty;

            details.Add($"{placement.Leg.Name.PadRight(width)}  runs on {host.Host} ({host.Os} {host.Processor}){through}");
        }

        return new CommandOutcome(exitCode, message, details);
    }

    /// <summary>The room on <paramref name="host"/>, or why it could not be measured; <see langword="null"/> where it was not asked.</summary>
    private static string? Room(HostReport host)
        => host.Space is { } space
            ? space.Describe()
            : host.SpaceUnmeasured is { } why
                ? $"the room there could not be measured: {why}"
                : null;

    private static string Summary(LegsReport report, IReadOnlyList<HostReport> silent)
    {
        if (report.Placements.Count == 0)
        {
            return "no legs are declared; add them under \"legs\"";
        }

        var runnable = report.Placements.Count(placement => placement.Runnable);
        var counted = $"{runnable} of {report.Placements.Count} leg(s) can run";

        if (report.Defect is { } defect)
        {
            return $"{counted}; whether leg '{defect.Leg.Name}' can run was never established, "
                + $"through a defect in this tool: {defect.Reason}";
        }

        if (report.Passed)
        {
            // Each host is named with its own reason rather than under one verb for all of them.
            // "Did not answer" was said of every host with a reason, including one that answered
            // and turned out to need a newer SDK, and one that was never asked because its
            // connection data is missing - so a host busy with a run read as unreachable, and
            // nothing on this line could say which. Only the reason knows the cause, so the line
            // carries it rather than a class this file would have to guess from the wording.
            return silent.Count == 0
                ? counted
                : $"{counted}; {silent.Count} host(s) cannot take legs, so this survey is incomplete: "
                    + string.Join("; ", silent.Select(host => $"{host.Host} ({host.Reason})"));
        }

        return runnable == 0
            ? $"{counted}: no selected leg can run on any host"
            : $"{counted}: a leg named with --legs cannot run";
    }
}
