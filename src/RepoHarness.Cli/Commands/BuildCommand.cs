using System.CommandLine;
using System.Diagnostics;
using RepoHarness.Core.Build;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Runs;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness build</c>.</summary>
internal static class BuildCommand
{
    internal const string Name = BuildService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, every declared leg.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    private static readonly Option<bool> TimeOption = new("--time")
    {
        Description = "Report the profile timing, taken from buildTimingRegex where it is configured and measured here where it is not.",
    };

    private static readonly Option<bool> ForceLockOption = new("--force-lock")
    {
        Description = "Take a lock a run on another host holds. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = new("--use-staged")
    {
        Description = "Build what is already staged on each host, without syncing again.",
    };

    private static readonly Option<bool> HereOption = new(RemoteLegRunner.HereOption)
    {
        Description = "Run every selected leg on this machine rather than on the host it was placed on.",
        Hidden = true,
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Build every selected leg, each in its own variant-keyed build directory, and check that it produced what the project declares.");

        command.Options.Add(LegsOption);
        command.Options.Add(JsonOption);
        command.Options.Add(TimeOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(HereOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            var builds = context.Get<IBuildService>();

            return await context.Get<LegRunService>()
                .RunAsync(
                    Name,
                    new LegRunRequest(
                        context.Directory,
                        legs,
                        arguments.GetValue(ForceLockOption),
                        arguments.GetValue(JsonOption),
                        arguments.GetValue(UseStagedOption),
                        arguments.GetValue(TimeOption),
                        arguments.GetValue(HereOption),
                        RemoteArguments(arguments))
                    {
                        Workload = LegWorkload.BuildOnly,
                    },
                    (work, token) => BuildLegAsync(builds, work, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }, JsonOption));

        return command;
    }

    private static async Task<LegEntry> BuildLegAsync(
        IBuildService builds,
        LegWork work,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var leg = work.Leg;

        var result = await builds
            .BuildAsync(
                work.Context.Config,
                new BuildRequest(
                    leg.Name,
                    leg.TreeRoot,
                    leg.BuildableProject(),
                    leg.Variant,
                    leg.Host.Os ?? string.Empty,
                    CoreCounts.Resolve(null, leg.HostSettings.BuildCores, work.Context.Config.Defaults.BuildCores).Value,
                    work.RunDirectory,
                    work.Time)
                {
                    ProgramDirectories = leg.Host.ProgramDirectories,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return new LegEntry
        {
            Leg = leg.Name,
            Verdict = result.Verdict.Verdict,
            Detail = Detail(result),
            Duration = Stopwatch.GetElapsedTime(started),
            CommandTime = result.Phases.Aggregate(TimeSpan.Zero, (total, phase) => total + phase.Duration),
            Emulated = leg.Emulated,
            Phases = [.. result.Phases.Select(phase => new PhaseRecord(phase.Phase, phase.Duration, phase.ClockStepped))],
            TimingNotes = [.. Notes(result)],
            Timings = [.. result.Phases.SelectMany(phase =>
                phase.Timings.Select(timing => new TimingMark(phase.Phase, timing.Text, timing.Value)))],
        };
    }

    /// <summary>
    /// The options a host running one of this run's legs is given, so it runs the command this
    /// machine was asked to run rather than a bare one.
    /// </summary>
    /// <remarks>
    /// <c>--legs</c> and <c>--json</c> are supplied by the dispatch itself, and the lock and the
    /// staging are this machine's decisions about its own state: a host reached for one leg takes
    /// its own lock and acts on the copy sync just wrote.
    /// </remarks>
    private static IReadOnlyList<string> RemoteArguments(System.CommandLine.ParseResult arguments)
        => arguments.GetValue(TimeOption) ? ["--time"] : [];

    private static string Detail(BuildResult result)
        => result.Verdict.Detail.Length > 0
            ? result.Verdict.Detail
            : result.Dependencies is { } dependencies && dependencies.Excused.Count > 0
                ? $"{dependencies.ObjectsRead} object(s) read, {dependencies.Excused.Count} excused"
                : string.Empty;

    private static IEnumerable<string> Notes(BuildResult result)
    {
        if (result.RebuiltFromClean is { } reason)
        {
            yield return "rebuilt from clean: " + reason;
        }

        foreach (var phase in result.Phases.Where(phase => phase.ClockStepped))
        {
            yield return $"{phase.Phase} spanned a clock step, so its duration and every mtime it wrote are suspect";
        }
    }
}
