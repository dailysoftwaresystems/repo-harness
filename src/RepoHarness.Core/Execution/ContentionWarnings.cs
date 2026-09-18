using System.Globalization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Execution;

/// <summary>
/// What a leg's contention report says to a reader, besides a contender: which other work ran
/// beside it, and what sampling could not see.
/// </summary>
/// <remarks>
/// Said once, here, for every verb that samples. Build and test each composed it for themselves,
/// word for word, and run composed it not at all, so a runner step's shared-state sightings were
/// dropped without a word.
/// </remarks>
public static class ContentionWarnings
{
    /// <summary>Writes what <paramref name="report"/> found for <paramref name="leg"/>.</summary>
    /// <param name="output">Where warnings go.</param>
    /// <param name="commandName">The verb, which prefixes each line.</param>
    /// <param name="leg">The leg the report is about.</param>
    /// <param name="report">What sampling found.</param>
    /// <param name="contention">The configuration, which may say what each tool shares.</param>
    /// <remarks>
    /// One line per tool and per whose it was, never one per process. Measured on a consumer's gate:
    /// 778 lines for two causes, 597 of them one sibling leg's compilers, burying whatever else a
    /// reader was meant to see. And a process working in another leg's build directory is named as
    /// that leg's, because the cause of the load - two legs on one host - is then the reader's to
    /// see and decide about, where "outside this run" sent them after a stranger that did not exist.
    /// </remarks>
    public static void Write(
        IHarnessOutput output,
        string commandName,
        string leg,
        ContentionReport report,
        ContentionConfig contention)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(contention);

        foreach (var line in SharedLines(leg, report, contention))
        {
            output.Warn(commandName, line);
        }

        foreach (var unreadable in report.Unreadable)
        {
            // Reported as unknown, never as nothing found: "no contender was running" and "nobody
            // looked" are different facts and only one of them is evidence.
            output.Warn(commandName, $"{leg}: the process table was not read for one sample ({unreadable}).");
        }

        foreach (var limit in report.Limits)
        {
            output.Detail(commandName, $"{leg}: sampling cannot see {limit}");
        }
    }

    /// <summary>One line for each tool and each leg - or no leg - its processes belonged to.</summary>
    /// <param name="leg">The leg the report is about.</param>
    /// <param name="report">What sampling found.</param>
    /// <param name="contention">The configuration, which may say what each tool shares.</param>
    public static IReadOnlyList<string> SharedLines(string leg, ContentionReport report, ContentionConfig contention)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(contention);

        return [.. report.SharedResourceUsers
            .GroupBy(user => (user.Tool, user.Owner))
            .OrderBy(group => group.Key.Owner is null ? 1 : 0)
            .ThenBy(group => group.Key.Owner, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Tool, StringComparer.Ordinal)
            .Select(group => Line(leg, group.Key.Tool, group.Key.Owner, [.. group], contention))];
    }

    private static string Line(
        string leg,
        string tool,
        string? owner,
        IReadOnlyList<ContendingProcess> users,
        ContentionConfig contention)
    {
        var ids = users.Select(user => user.Process.Id).Order().ToList();

        var processes = ids.Count == 1
            ? $"pid {ids[0].ToString(CultureInfo.InvariantCulture)}"
            : $"{ids.Count.ToString(CultureInfo.InvariantCulture)} processes, pids "
                + $"{ids[0].ToString(CultureInfo.InvariantCulture)}-{ids[^1].ToString(CultureInfo.InvariantCulture)}";

        var seen = string.Join(", ", users.Select(user => ContentionReport.Describe(user.Seen)).Distinct(StringComparer.Ordinal));

        var whose = owner is null
            ? "which no declared leg's build directory accounts for"
            : $"working in leg '{owner}''s build directory";

        var state = contention.SharedState.TryGetValue(tool, out var named) && !string.IsNullOrWhiteSpace(named)
            ? $"it shares {named}"
            : "it shares state outside any build directory - say which under contention.sharedState";

        var consequence = owner is null
            ? string.Empty
            : $", so this leg and '{owner}' load one host and contend for it";

        return $"{leg}: {tool} ({processes}), {whose}, ran beside this leg, seen {seen}; {state}{consequence}.";
    }
}
