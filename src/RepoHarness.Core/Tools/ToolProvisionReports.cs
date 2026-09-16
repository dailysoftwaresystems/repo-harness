using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Tools;

/// <summary>Turns what provisioning found into what <c>install-missing-tools</c> prints.</summary>
/// <remarks>
/// JSON is written alone on standard output, with every host that could not be reached warned about on
/// standard error while it happened, so the document always parses.
/// </remarks>
public static class ToolProvisionReports
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What <c>install-missing-tools</c> reports.</summary>
    /// <param name="report">What provisioning found.</param>
    /// <param name="json">Whether the answer is a document rather than a table.</param>
    public static CommandOutcome Render(ToolProvisionReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        var exitCode = report.Passed
            ? HarnessExit.Success
            : report.AnyUnreachable ? HarnessExit.HostUnavailable : ToolsExit.NotProvisioned;

        var message = Summary(report);

        if (json)
        {
            var document = new
            {
                Legs = report.Legs.Select(leg => new
                {
                    leg.Leg,
                    Host = leg.Host.ToString(),
                    leg.Provisioned,
                    leg.Unreachable,
                    Tools = leg.Tools.Select(tool => new
                    {
                        tool.Tool,
                        State = tool.StateName,
                        tool.Version,
                        tool.Detail,
                    }),
                }),
            };

            return new CommandOutcome(exitCode, message)
            {
                Data = [JsonSerializer.Serialize(document, JsonOptions)],
                Quiet = true,
            };
        }

        var details = new List<string>();
        var width = report.Legs.Count == 0 ? 0 : report.Legs.Max(leg => leg.Leg.Length);

        foreach (var leg in report.Legs)
        {
            var name = leg.Leg.PadRight(width);

            if (leg.Unreachable is { } unreachable)
            {
                details.Add($"{name}  {leg.Host}: not reached: {unreachable}");
                continue;
            }

            if (leg.Tools.Count == 0)
            {
                details.Add($"{name}  {leg.Host}: nothing to install");
                continue;
            }

            foreach (var tool in leg.Tools)
            {
                var version = tool.Version is { Length: > 0 } found ? $" {found}" : string.Empty;
                var detail = tool.Detail is { Length: > 0 } why ? $": {why}" : string.Empty;

                details.Add($"{name}  {leg.Host}: {tool.Tool}{version} {tool.StateName}{detail}");
            }
        }

        return new CommandOutcome(exitCode, message, details);
    }

    private static string Summary(ToolProvisionReport report)
    {
        if (report.Legs.Count == 0)
        {
            return "no legs are declared; add them under \"legs\"";
        }

        var ready = report.Legs.Count(leg => leg.Provisioned);
        var counted = $"{ready} of {report.Legs.Count} leg(s) have every tool they need";

        if (report.Passed)
        {
            return counted;
        }

        return report.AnyUnreachable
            ? $"{counted}: a host could not be reached"
            : $"{counted}: a tool is missing, out of date, or could not be installed";
    }
}
