using System.CommandLine;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Runners;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness legs</c>.</summary>
internal static class LegsCommand
{
    internal const string Name = LegsService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, every declared leg.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write where each leg runs, and every host measured, as JSON.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Measure the hosts and show where each leg can run, or why it cannot. Runs each emulator's witness, and installs or updates DssHarness on hosts that are behind.");

        command.Options.Add(LegsOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            // Left out, --legs selects every leg; given, it must name one. Which of the two happened is told by
            // whether the option appeared at all, never by what its value holds.
            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            // Checked here, and again when a runner is finally invoked. 'legs' answers whether this
            // repository can run what it declares, so an action nobody can read is an answer of no
            // — given now, rather than left for the run that finds out.
            var harness = await context.Get<IHarnessContextLoader>()
                .LoadAsync(context.Directory, cancellationToken)
                .ConfigureAwait(false);

            ActionPath.RequireResolvable(
                harness.Config.ActionsByRunner(),
                harness.Layout.RunnerActionsDirectory,
                context.Get<IFileSystem>(),
                context.Get<IHostPlatform>().PathComparison);

            var report = await context.Get<LegsService>()
                .CheckAsync(context.Directory, legs, LegWorkload.BuildAndTest, here: null, cancellationToken)
                .ConfigureAwait(false);

            return LegsReports.Render(report, arguments.GetValue(JsonOption), arguments.GetValue(GlobalOptions.Verbose));
        }));

        return command;
    }
}
