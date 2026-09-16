using System.CommandLine;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness check-root-litter</c>.</summary>
internal static class CheckRootLitterCommand
{
    internal const string Name = RootLitterService.CommandName;

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Report files left loose at the root of the checkout, ignored ones included, and entries whose names hold a path from another machine.");

        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var report = await context.Get<IRootLitterService>()
                .CheckAsync(context.Directory, cancellationToken)
                .ConfigureAwait(false);

            if (report.IsClean)
            {
                return CommandOutcome.Ok($"no loose files at the root of {report.Root}");
            }

            // A refusal rather than a failure: nothing ran and failed, a precondition was not met,
            // and the remedy is to move or delete the files by name. Deleting them is deliberately
            // left to the reader, because one of them is occasionally work.
            return CommandOutcome.Refused(
                $"{report.All.Count} loose entr{(report.All.Count == 1 ? "y" : "ies")} at the root of {report.Root}",
                [.. report.All]);
        }));

        return command;
    }
}
