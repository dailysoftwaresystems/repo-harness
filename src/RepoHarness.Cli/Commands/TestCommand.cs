using System.CommandLine;
using System.Diagnostics;
using RepoHarness.Core.Build;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Testing;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness test</c>.</summary>
internal static class TestCommand
{
    internal const string Name = TestService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, every declared leg.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<string> FilterOption = new("--filter")
    {
        Description = "Run only tests the invocation's filterArg selects with this value.",
    };

    private static readonly Option<string[]> ExcludeOption = new("--exclude")
    {
        Description = "Skip tests the invocation's excludeArg excludes with these values.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    private static readonly Option<bool> TimeOption = new("--time")
    {
        Description = "Report the profile timing, taken from testTimingRegex where it is configured and measured here where it is not.",
    };

    private static readonly Option<bool> ForceLockOption = new("--force-lock")
    {
        Description = "Take a lock a run on another host holds. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = new("--use-staged")
    {
        Description = "Test what is already staged on each host, without syncing again.",
    };

    private static readonly Option<bool> SkipBuildOption = new("--no-build")
    {
        Description = "Test what is already built, without building first.",
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
            "Build and test every selected leg, with a witness for each verdict: a zero exit code with no match, a tree that moved, or another run in the build directory each report as themselves.");

        command.Options.Add(LegsOption);
        command.Options.Add(FilterOption);
        command.Options.Add(ExcludeOption);
        command.Options.Add(JsonOption);
        command.Options.Add(TimeOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(SkipBuildOption);
        command.Options.Add(HereOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            var builds = context.Get<IBuildService>();
            var tests = context.Get<ITestService>();
            var filter = arguments.GetValue(FilterOption);
            var excludes = arguments.GetValue(ExcludeOption) ?? [];
            var skipBuild = arguments.GetValue(SkipBuildOption);

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
                        // Built first unless told not to, and tested either way.
                        Workload = new LegWorkload(Build: !skipBuild, Test: true, []),
                    },
                    (work, token) => RunLegAsync(builds, tests, work, filter, excludes, skipBuild, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }));

        return command;
    }

    /// <summary>
    /// The options a host running one of this run's legs is given, so it runs the command this
    /// machine was asked to run rather than a bare one.
    /// </summary>
    /// <remarks>
    /// A filter and an exclusion decide which tests run, so a host that did not get them would run
    /// a different suite and report its result under the same leg's name. <c>--legs</c> and
    /// <c>--json</c> are the dispatch's own; the lock and the staging are this machine's decisions
    /// about its own state.
    /// </remarks>
    private static IReadOnlyList<string> RemoteArguments(System.CommandLine.ParseResult arguments)
    {
        var remote = new List<string>();

        if (arguments.GetValue(FilterOption) is { Length: > 0 } filter)
        {
            remote.Add("--filter");
            remote.Add(filter);
        }

        foreach (var exclude in arguments.GetValue(ExcludeOption) ?? [])
        {
            remote.Add("--exclude");
            remote.Add(exclude);
        }

        if (arguments.GetValue(SkipBuildOption))
        {
            remote.Add("--no-build");
        }

        if (arguments.GetValue(TimeOption))
        {
            remote.Add("--time");
        }

        return remote;
    }

    /// <summary>
    /// Builds the leg, then tests it. The order is fixed, and a build that did not pass ends the leg
    /// there: tests run against whatever the build left behind, so testing after a failed build
    /// reports on a binary nobody can name.
    /// </summary>
    private static async Task<LegEntry> RunLegAsync(
        IBuildService builds,
        ITestService tests,
        LegWork work,
        string? filter,
        IReadOnlyList<string> excludes,
        bool skipBuild,
        CancellationToken cancellationToken)
    {
        var leg = work.Leg;
        var config = work.Context.Config;
        var started = Stopwatch.GetTimestamp();

        if (!skipBuild)
        {
            var build = await builds
                .BuildAsync(
                    config,
                    new BuildRequest(
                        leg.Name,
                        leg.TreeRoot,
                        leg.BuildableProject(),
                        leg.Variant,
                        leg.Host.Os ?? string.Empty,
                        CoreCounts.Resolve(null, leg.HostSettings.BuildCores, config.Defaults.BuildCores).Value,
                        work.RunDirectory)
                    {
                        ProgramDirectories = leg.Host.ProgramDirectories,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (build.Verdict.Verdict != LegVerdict.Passed)
            {
                return new LegEntry
                {
                    Leg = leg.Name,
                    Verdict = build.Verdict.Verdict,
                    Detail = build.Verdict.Detail,
                    Duration = Stopwatch.GetElapsedTime(started),
                    Emulated = leg.Emulated,
                };
            }
        }

        // Derived once from the placed leg, the same way the runner derives it.
        var (testProduct, testProductProblem) = leg.ProductFor(leg.BuildDirectory);

        var result = await tests
            .RunAsync(
                config,
                new TestRequest
                {
                    Leg = leg.Name,
                    ProgramDirectories = leg.Host.ProgramDirectories,
                    TreeRoot = leg.TreeRoot,
                    BuildDirectory = leg.BuildDirectory,
                    RunDirectory = work.RunDirectory,
                    LegSettings = leg.Leg,
                    Project = leg.Project,
                    PlatformKey = leg.Host.Os ?? string.Empty,
                    Identity = leg.IdentityFor(work.RunId.Value),
                    Product = testProduct,
                    ProductProblem = testProductProblem,
                    HostTestCores = leg.HostSettings.TestCores,
                    Filter = filter,
                    Excludes = excludes,
                    Emulated = leg.Emulated,
                    Time = work.Time,
                },
                cancellationToken)
            .ConfigureAwait(false);

        // The leg's whole duration, not the runner's: the build, the fingerprints and the sampling
        // are what the ledger reports as overhead, and leaving them out would hide them.
        return result.Entry with { Duration = Stopwatch.GetElapsedTime(started) };
    }
}
