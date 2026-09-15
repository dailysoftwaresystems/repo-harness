using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public static CommandOutcome Render(LegsReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        var exitCode = report.Passed ? HarnessExit.Success : LegsExit.Unavailable;
        var message = Summary(report);

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
                    RepoHarness = host.ToolVersion,
                    host.Actions,
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

    private static string Summary(LegsReport report)
    {
        if (report.Placements.Count == 0)
        {
            return "no legs are declared; add them under \"legs\"";
        }

        var runnable = report.Placements.Count(placement => placement.Runnable);
        var counted = $"{runnable} of {report.Placements.Count} leg(s) can run";

        if (report.Passed)
        {
            return counted;
        }

        return runnable == 0
            ? $"{counted}: no selected leg can run on any host"
            : $"{counted}: a leg named with --legs cannot run";
    }
}
