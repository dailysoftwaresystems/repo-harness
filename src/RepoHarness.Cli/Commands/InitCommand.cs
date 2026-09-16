using System.CommandLine;
using RepoHarness.Core.Commands;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness init</c>. Orchestration only; the work is in the service.</summary>
internal static class InitCommand
{
    internal const string Name = "init";

    /// <summary>Builds the command.</summary>
    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Create .harness-config with its configuration, its worktrees, runner and connection-data directories, and the ignore rules that keep the untracked ones out of git.");

        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) =>
            context.Get<InitService>().InitializeAsync(context.Directory, cancellationToken)));

        return command;
    }
}
