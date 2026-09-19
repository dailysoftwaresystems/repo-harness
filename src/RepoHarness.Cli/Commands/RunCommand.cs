using System.CommandLine;
using System.Diagnostics;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Legs;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;
using RepoHarness.Core.Runs;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness run</c>.</summary>
internal static class RunCommand
{
    internal const string Name = RunnerRunService.CommandName;

    private static readonly Argument<string> RunnerArgument = new("runner")
    {
        Description = "The predefined runner to run, as predefinedRunners names it.",
    };

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, the legs the runner declares.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    private static readonly Option<bool> TimeOption = new("--time")
    {
        Description = "Pull runTimingRegex out of every step's output.",
    };

    private static readonly Option<bool> ForceLockOption = new("--force-lock")
    {
        Description = "Take a lock a run on another host holds. Always a human decision.",
    };

    private static readonly Option<bool> UseStagedOption = new("--use-staged")
    {
        Description = "Run against what is already staged on each host, without syncing again.",
    };

    private static readonly Option<string[]> InputOption = new(CommandLineInputs.Option)
    {
        Description = "Give one of the action's inputs a value for this run: --input name=value, once per input. Over the runner's .env values and the input's default; never a secret.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Run a predefined runner across the legs it declares, with the same isolation, locking, stall bounds, witnesses and reporting build and test get.");

        command.Arguments.Add(RunnerArgument);
        command.Options.Add(LegsOption);
        command.Options.Add(JsonOption);
        command.Options.Add(TimeOption);
        command.Options.Add(ForceLockOption);
        command.Options.Add(UseStagedOption);
        command.Options.Add(InputOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;
            var runnerName = arguments.GetRequiredValue(RunnerArgument);

            IReadOnlyList<string>? named = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            // Read before anything else is: a pair that gives nothing is a mistake on this line
            // alone, and costs no configuration read to name.
            var inputs = CommandLineInputs.Parse(arguments.GetValue(InputOption) ?? []);

            var runners = context.Get<IRunnerRunService>();
            var builds = context.Get<IBuildService>();

            var harness = await context.Get<RepoHarness.Core.Repository.IHarnessContextLoader>()
                .LoadAsync(context.Directory, cancellationToken)
                .ConfigureAwait(false);

            var runner = Resolve(harness.Config, runnerName);
            ActionFile? file = null;

            // Before a leg is placed or a host is measured, so that a mistyped action costs nothing
            // and says so in the same terms 'legs' would have.
            if (runner.Action is { Length: > 0 } action)
            {
                ActionPath.RequireResolvable(
                    [new KeyValuePair<string, string>(runnerName, action)],
                    harness.Layout.RunnerActionsDirectory,
                    context.Get<IFileSystem>(),
                    context.Get<IHostPlatform>().PathComparison);

                // And read here, not only once a leg is running it. Read per leg, a file carrying
                // one unrecognised key is refused once per leg, each time after that leg's run has
                // begun and its run directory exists: on an eight-leg gate that is eight started
                // runs and eight directories for one typo. The file is the same for every leg, so
                // the question is asked once, where nothing has been created yet.
                file = await context.Get<IActionFileParser>()
                    .LoadAsync(harness.Layout.RunnerActionsDirectory, action, cancellationToken)
                    .ConfigureAwait(false);
            }

            // And every value given goes to an input the file declares, asked here for the same
            // reason: once, before any leg's run has begun.
            CommandLineInputs.RequireDeclared(runnerName, file, inputs);

            // The runner's own legs when --legs was left out. Resolved here rather than left to the
            // default of every declared leg, because running a benchmark on hosts nobody meant to
            // measure is not what "no --legs" asks for.
            var declared = runner.Legs;
            var selected = named ?? (declared.Count > 0 ? declared : null);

            // A leg whose operating system no step runs on would run nothing and pass. Refused here,
            // like a mistyped action, before a host is measured and naming every such leg at once,
            // rather than once per leg after each one's run has begun.
            file?.RequireAStepOn(
                runnerName,
                LegSelection.Resolve(harness.Config, selected).Legs.Select(leg => (leg.Name, leg.Leg.Os)));

            return await context.Get<LegRunService>()
                .RunAsync(
                    Name,
                    new LegRunRequest(
                        context.Directory,
                        selected,
                        arguments.GetValue(ForceLockOption),
                        arguments.GetValue(JsonOption),
                        arguments.GetValue(UseStagedOption),
                        arguments.GetValue(TimeOption),
                        arguments.GetValue(DispatchOptions.Here),
                        RunnerRunService.RemoteArguments(runnerName, arguments.GetValue(TimeOption), inputs))
                    {
                        // Built only where the runner requires it, and never tested: a host needs
                        // cmake for a runner that measures a build product, and not for one that
                        // only runs a script.
                        Workload = LegWorkload.ForRunner(runner, file),
                    },
                    (work, token) => RunLegAsync(runners, builds, runnerName, inputs, work, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }, JsonOption));

        return command;
    }

    private static RunnerConfig Resolve(HarnessConfig config, string runnerName)
    {
        var declared = DeclaredName.In(config.PredefinedRunners.Keys, runnerName);

        return declared is null
            ? throw new HarnessException(
                HarnessExit.UsageError,
                $"'{runnerName}' is not declared under predefinedRunners. Declared: "
                + $"{string.Join(", ", config.PredefinedRunners.Keys.Order(StringComparer.Ordinal))}.")
            : config.PredefinedRunners[declared];
    }

    private static async Task<LegEntry> RunLegAsync(
        IRunnerRunService runners,
        IBuildService builds,
        string runnerName,
        IReadOnlyDictionary<string, string> inputs,
        LegWork work,
        CancellationToken cancellationToken)
    {
        var leg = work.Leg;
        var config = work.Context.Config;
        var runner = Resolve(config, runnerName);
        var started = Stopwatch.GetTimestamp();

        // Built before the runner starts, when the runner says it needs the compiler. Otherwise it
        // calls a program the build produces and runs against whatever was left there last time.
        if (runner.RequireBuild)
        {
            var build = await builds
                .BuildAsync(config, leg.BuildRequestFor(config, work.RunDirectory), cancellationToken)
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

        var result = await runners
            .RunAsync(
                config,
                RequestFor(work, runnerName, runner) with
                {
                    Time = work.Time,

                    // Only the runner the command line named: the values were checked against its
                    // action's inputs, and a runner a check starts reads its own.
                    Inputs = inputs,

                    // One level deep by construction: the runner a check names carries no checks of
                    // its own, and this delegate reaches the service only for that one.
                    InvokeRunner = (name, token) => ConfirmAsync(runners, config, work, name, token),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return result.Entry with { Duration = Stopwatch.GetElapsedTime(started) };
    }

    /// <summary>
    /// Runs the runner a check names, for the gate that confirms an expected exception.
    /// </summary>
    /// <remarks>
    /// One level deep, and enforced here as well as by the configuration: the runner this reaches is
    /// given no way to invoke another, so even a configuration that slipped past validation cannot
    /// make a check confirm itself. A runner reached this way needs no build of its own — the leg was
    /// already built for the runner carrying the check, and rebuilding it mid-run would replace the
    /// binaries the failure being explained came from.
    /// </remarks>
    private static async Task<RunOutcome> ConfirmAsync(
        IRunnerRunService runners,
        HarnessConfig config,
        LegWork work,
        string runnerName,
        CancellationToken cancellationToken)
    {
        var result = await runners
            .RunAsync(config, RequestFor(work, runnerName, Resolve(config, runnerName)), cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome;
    }

    /// <summary>
    /// A run of <paramref name="runner"/> on the leg <paramref name="work"/> carries, with what the
    /// leg's host declares for it. Made once, for the runner a leg runs and for the one a check names,
    /// so what the host declares reaches both or neither.
    /// </summary>
    private static RunnerRunRequest RequestFor(LegWork work, string runnerName, RunnerConfig runner)
    {
        var leg = work.Leg;

        // Derived from the very directory this request carries, so a run line naming {product} and
        // one naming {buildDir} cannot come from two different tree roots. They do differ: a leg
        // placed on another machine re-invokes this there, where its own tree is the one that
        // resolves.
        var buildDirectory = leg.Variant.DirectoryUnder(leg.TreeRoot);
        var (product, productProblem) = leg.ProductFor(buildDirectory);

        return new RunnerRunRequest
        {
            ProgramDirectories = leg.Host.ProgramDirectories,
            HostEnvironment = leg.HostSettings.Env,
            RunnerName = runnerName,
            Runner = runner,
            Leg = leg.Name,
            Layout = work.Context.Layout,
            RunId = work.RunId.Value,

            // A new segment each attempt, so resuming a run cannot record its work into the
            // attempt it is resuming.
            SegmentId = Guid.NewGuid().ToString("N")[..8],
            TreeRoot = leg.TreeRoot,
            WorkingDirectory = leg.TreeRoot,
            BuildDirectory = buildDirectory,
            Identity = leg.IdentityFor(work.RunId.Value),
            Product = product,
            ProductProblem = productProblem,
            ResolvedLegs = [leg.Name],
            Emulated = leg.Emulated,
        };
    }
}
