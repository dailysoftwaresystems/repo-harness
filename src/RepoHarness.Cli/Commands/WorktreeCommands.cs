using System.CommandLine;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness create-worktree</c>.</summary>
internal static class CreateWorktreeCommand
{
    internal const string Name = "create-worktree";

    private static readonly Argument<string?> NameArgument = new("name")
    {
        Description = $"Worktree name: lowercase letters and digits joined by hyphens; at most {WorktreeSettings.DefaultMaxNameLength} characters unless worktrees.maxNameLength says otherwise.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<bool> RandomOption = new("--random")
    {
        Description = "Generate the name instead of supplying one; the chosen name is reported.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "Create a worktree under .harness-config/worktrees.");
        command.Arguments.Add(NameArgument);
        command.Options.Add(RandomOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var result = await context.Get<IWorktreeService>()
                .CreateAsync(
                    context.Directory,
                    context.ParseResult.GetValue(NameArgument),
                    context.ParseResult.GetValue(RandomOption),
                    cancellationToken)
                .ConfigureAwait(false);

            return result.Outcome;
        }));

        return command;
    }
}

/// <summary>Wires <c>DssHarness delete-worktree</c>.</summary>
internal static class DeleteWorktreeCommand
{
    internal const string Name = "delete-worktree";

    /// <summary>
    /// How long an interrupted delete-worktree may still run before the process ends. Past its point
    /// of no return a deletion runs to the end rather than leave a worktree half deleted, and git
    /// removing a large tree on Windows can take far longer than the two seconds System.CommandLine
    /// otherwise waits. Two minutes lets that finish, and still ends a command that has hung.
    /// </summary>
    internal static readonly TimeSpan InterruptionGrace = TimeSpan.FromMinutes(2);

    private static readonly Argument<string> NameArgument = new("name")
    {
        Description = "Worktree to remove.",
    };

    private static readonly Option<bool> ForceOption = new("--force")
    {
        Description = "Delete the worktree without checking it, even when locked: uncommitted changes, commits on no branch, tag, remote-tracking ref or newest stash, and submodules' unpushed work are lost.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "Remove a worktree and everything under it; refuses one holding work that would be lost, or a locked one, without --force.");
        command.Arguments.Add(NameArgument);
        command.Options.Add(ForceOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var result = await context.Get<IWorktreeService>()
                .DeleteAsync(
                    context.Directory,
                    context.ParseResult.GetRequiredValue(NameArgument),
                    context.ParseResult.GetValue(ForceOption),
                    cancellationToken)
                .ConfigureAwait(false);

            return result.Outcome;
        }));

        return command;
    }
}

/// <summary>Wires <c>DssHarness list-worktree</c>.</summary>
internal static class ListWorktreeCommand
{
    internal const string Name = "list-worktree";

    internal static Command Create()
    {
        var command = new Command(Name, "List existing worktrees.");
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var names = await context.Get<IWorktreeService>()
                .ListAsync(context.Directory, cancellationToken)
                .ConfigureAwait(false);

            return names.Count == 0
                ? CommandOutcome.Ok("no worktrees")
                : CommandOutcome.Ok($"{names.Count} worktree(s)", [.. names]);
        }));

        return command;
    }
}
