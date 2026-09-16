using System.CommandLine;
using RepoHarness.Core.Ci;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness check-ci-legs</c>.</summary>
internal static class CheckCiLegsCommand
{
    internal const string Name = "check-ci-legs";

    private static readonly Option<string?> BranchOption = new("--branch")
    {
        Description = "Ask about this branch instead of the one checked out.",
    };

    private static readonly Option<int> LimitOption = new("--limit")
    {
        Description = "How many runs of each workflow to read, newest first.",
        DefaultValueFactory = _ => CiLegsRequest.DefaultLimit,
    };

    private static readonly Option<long[]> RunOption = new("--run")
    {
        Description = "Read these run ids, in the order given, instead of the newest runs.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg result as JSON: success, errors, and whether a failure is a possible budget overrun.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Read the CI verdict of each leg from job metadata, which outlives the logs, and separate a real failure from a budget overrun. Reads only; it never labels, re-runs or pushes.");

        command.Options.Add(BranchOption);
        command.Options.Add(LimitOption);
        command.Options.Add(RunOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var request = new CiLegsRequest(
                arguments.GetValue(BranchOption),
                arguments.GetValue(LimitOption),
                arguments.GetValue(RunOption) ?? []);

            var report = await context.Get<ICiLegsService>()
                .CheckAsync(context.Directory, request, cancellationToken)
                .ConfigureAwait(false);

            return CiLegsReports.Render(report, arguments.GetValue(JsonOption));
        }));

        return command;
    }
}
