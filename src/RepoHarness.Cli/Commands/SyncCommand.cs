using System.CommandLine;
using RepoHarness.Core.Sync;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness sync</c>.</summary>
internal static class SyncCommand
{
    internal const string Name = SyncService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only the hosts these legs or leg sets run on: --legs a,b or --legs a b. Without it, every host a declared leg needs.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> DryRunOption = new("--dry-run")
    {
        Description = "List what would be written and deleted, and change nothing.",
    };

    private static readonly Option<string[]> AdoptOption = new("--adopt")
    {
        Description = "Take over these hosts' copies although the harness did not create them: --adopt vps or --adopt vps \"wsl Ubuntu\". Otherwise refused. What it would cost is reported either way.",
        AllowMultipleArgumentsPerToken = true,
        Arity = ArgumentArity.OneOrMore,
    };

    private static readonly Option<string[]> PullOption = new("--pull")
    {
        Description = "Bring these paths back from each host's copy instead of syncing to it, verified on arrival.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<string> ArtifactOption = new("--artifact")
    {
        Description = "Carry one run's kept artifacts to each host instead of syncing the tree: --artifact 20260917-100000-0a1b2c3d. What a step kept is what a later step on another host reads.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Put each host's copy of this repository in step with this tree: files whose content changed are written, files the source no longer has are deleted, and the copy is verified afterwards.");

        command.Options.Add(LegsOption);
        command.Options.Add(DryRunOption);
        command.Options.Add(AdoptOption);
        command.Options.Add(PullOption);
        command.Options.Add(ArtifactOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            return await context.Get<ISyncService>()
                .SyncHostsAsync(
                    context.Directory,
                    legs,
                    new SyncOptions(arguments.GetValue(DryRunOption), arguments.GetValue(AdoptOption))
                    {
                        Artifact = arguments.GetValue(ArtifactOption),
                    },
                    arguments.GetValue(PullOption) ?? [],
                    cancellationToken)
                .ConfigureAwait(false);
        }));

        return command;
    }
}
