using System.CommandLine;
using RepoHarness.Core.Legs;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>repo-harness legs</c>.</summary>
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
            "Measure the hosts and show where each leg can run, or why it cannot. Runs each emulator's witness, and installs or updates repo-harness on hosts that are behind.");

        command.Options.Add(LegsOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var report = await context.Get<LegsService>()
                .CheckAsync(context.Directory, arguments.GetValue(LegsOption) ?? [], cancellationToken)
                .ConfigureAwait(false);

            return LegsReports.Render(report, arguments.GetValue(JsonOption));
        }));

        return command;
    }
}
