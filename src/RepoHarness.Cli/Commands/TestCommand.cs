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

    private static readonly Option<string[]> LabelOption = new("--label")
    {
        Description = "Run only tests the invocation's labelArg selects with these values, such as a ctest label.",
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

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Build and test every selected leg, with a witness for each verdict: a zero exit code with no match, a tree that moved, or another run in the build directory each report as themselves.");

        command.Options.Add(LegsOption);
        command.Options.Add(FilterOption);
        command.Options.Add(ExcludeOption);
        command.Options.Add(LabelOption);
        command.Options.Add(JsonOption);
        command.Options.Add(TimeOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(SkipBuildOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            var builds = context.Get<IBuildService>();
            var tests = context.Get<ITestService>();

            // Read once, for the legs run here and the hosts given theirs alike: which tests run is one
            // choice, and a host told something else would report another suite under the same leg.
            var filter = arguments.GetValue(FilterOption);
            var excludes = arguments.GetValue(ExcludeOption) ?? [];
            var labels = arguments.GetValue(LabelOption) ?? [];
            var skipBuild = arguments.GetValue(SkipBuildOption);
            var time = arguments.GetValue(TimeOption);

            return await context.Get<LegRunService>()
                .RunAsync(
                    Name,
                    new LegRunRequest(
                        context.Directory,
                        legs,
                        arguments.GetValue(ForceLockOption),
                        arguments.GetValue(JsonOption),
                        arguments.GetValue(UseStagedOption),
                        time,
                        arguments.GetValue(DispatchOptions.Here),
                        TestService.RemoteArguments(filter, excludes, labels, skipBuild, time))
                    {
                        // Built first unless told not to, and tested either way.
                        Workload = new LegWorkload(Build: !skipBuild, Test: true, []),
                    },
                    (work, token) => RunLegAsync(builds, tests, context.Get<CMakeToolchainReader>(), work, filter, excludes, labels, skipBuild, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }, JsonOption));

        return command;
    }

    /// <summary>
    /// Builds the leg, then tests it. The order is fixed, and a build that did not pass ends the leg
    /// there: tests run against whatever the build left behind, so testing after a failed build
    /// reports on a binary nobody can name.
    /// </summary>
    private static async Task<LegEntry> RunLegAsync(
        IBuildService builds,
        ITestService tests,
        CMakeToolchainReader toolchains,
        LegWork work,
        string? filter,
        IReadOnlyList<string> excludes,
        IReadOnlyList<string> labels,
        bool skipBuild,
        CancellationToken cancellationToken)
    {
        var leg = work.Leg;
        var config = work.Context.Config;
        var started = Stopwatch.GetTimestamp();

        // Derived once from the placed leg, the same way the runner derives it.
        var (testProduct, testProductProblem) = leg.ProductFor(leg.BuildDirectory);

        var request = new TestRequest
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
            HostEnvironment = leg.Environment,
            Filter = filter,
            Excludes = excludes,
            Labels = labels,
            Emulated = leg.Emulated,
            Time = work.Time,
        };

        // Refused before the build rather than after it: everything the command is made from is known now.
        tests.Check(config, request);

        // The compilers the binaries under test were built with: this build's, or - where the leg
        // tests a build it does not make - what its directory was last configured with.
        IReadOnlyList<CompilerFact> compilers;

        // What the build says beyond its verdict, which the leg's line carries as the build's own does.
        IReadOnlyList<string> built = [];

        if (!skipBuild)
        {
            var build = await builds
                .BuildAsync(config, leg.BuildRequestFor(config, work.RunDirectory), cancellationToken)
                .ConfigureAwait(false);

            compilers = build.Compilers;
            built = build.Notes;

            if (build.Verdict.Verdict != LegVerdict.Passed)
            {
                return new LegEntry
                {
                    Leg = leg.Name,
                    Verdict = build.Verdict.Verdict,
                    Detail = build.Verdict.Detail,
                    Duration = Stopwatch.GetElapsedTime(started),
                    Emulated = leg.Emulated,
                    TimingNotes = built,
                    Compilers = compilers,
                };
            }
        }
        else
        {
            // Held to the toolchain as the build that made the directory is: binaries a compiler nobody
            // chose produced are no more tested than they would have been built, and a declared compiler
            // nothing established is unwitnessed here as there.
            var reading = toolchains.Configured(leg.Project, leg.BuildDirectory);
            compilers = reading?.Compilers ?? [];

            if (reading is not null && CompilerFacts.HeldTo(config, leg.Variant.Toolchain, reading) is { } held)
            {
                return new LegEntry
                {
                    Leg = leg.Name,
                    Verdict = held.Verdict,
                    Detail = held.Detail,
                    Duration = Stopwatch.GetElapsedTime(started),
                    Emulated = leg.Emulated,
                    Compilers = compilers,
                };
            }
        }

        var result = await tests.RunAsync(config, request, cancellationToken).ConfigureAwait(false);

        // The leg's whole duration, not the runner's: the build, the fingerprints and the sampling
        // are what the ledger reports as overhead, and leaving them out would hide them.
        return result.Entry with
        {
            Duration = Stopwatch.GetElapsedTime(started),
            TimingNotes = [.. built, .. result.Entry.TimingNotes],
            Compilers = compilers,
        };
    }
}
