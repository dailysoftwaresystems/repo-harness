using System.CommandLine;
using RepoHarness.Core.Commands;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness init</c>. Orchestration only; the work is in the service.</summary>
internal static class InitCommand
{
    internal const string Name = "init";

    private static readonly Option<bool> InstallToolsOption = new("--install-tools")
    {
        Description = "Also install what each declared leg's host is missing, as install-missing-tools does. Off unless given.",
    };

    /// <summary>Builds the command.</summary>
    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Create .harness-config in the tree it runs in - a worktree's own included - with its configuration, its runner and connection-data directories, and the ignore rules that keep the untracked ones out of git.");

        command.Options.Add(InstallToolsOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) =>
            context.Get<InitService>().InitializeAsync(
                context.Directory,
                context.ParseResult.GetValue(InstallToolsOption),
                cancellationToken)));

        return command;
    }
}
