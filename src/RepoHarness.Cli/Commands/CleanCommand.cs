using System.CommandLine;
using RepoHarness.Core.Runs;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness clean</c>.</summary>
internal static class CleanCommand
{
    internal const string Name = CleanService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, every declared leg.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> DryRunOption = new(CleanService.DryRunOption)
    {
        Description = "Say what each leg's build directory holds and the room left on its filesystem, and remove nothing.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the per-leg ledger as JSON.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Remove each selected leg's build directory wherever the leg runs, writing nothing there first, so a full disk can be freed.");

        command.Options.Add(LegsOption);
        command.Options.Add(DryRunOption);
        command.Options.Add(JsonOption);
        command.Options.Add(DispatchOptions.Here);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            return await context.Get<CleanService>()
                .RunAsync(
                    new CleanRequest(
                        context.Directory,
                        legs,
                        arguments.GetValue(DryRunOption),
                        arguments.GetValue(JsonOption),
                        arguments.GetValue(DispatchOptions.Here)),
                    cancellationToken)
                .ConfigureAwait(false);
        }, JsonOption));

        return command;
    }
}
