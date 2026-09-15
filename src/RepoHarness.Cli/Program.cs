using System.CommandLine;
using RepoHarness.Cli;
using RepoHarness.Cli.Commands;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

// Command selection and dependency wiring only. Every behaviour lives in a service
// in RepoHarness.Core, which is a library precisely so that nothing can accumulate here.
using var consoleEncoding = ConsoleEncoding.UseUtf8();

var root = new RootCommand(
    "DssHarness - cross-platform repository harness. Run 'DssHarness help' for reference material.");

root.Subcommands.Add(InitCommand.Create());
root.Subcommands.Add(VerifyGitCommand.Create());
root.Subcommands.Add(CreateWorktreeCommand.Create());
root.Subcommands.Add(DeleteWorktreeCommand.Create());
root.Subcommands.Add(ListWorktreeCommand.Create());
root.Subcommands.Add(WriteAnchorCommand.Create());
root.Subcommands.Add(SetAnchorCommand.Create());
root.Subcommands.Add(ReadAnchorCommand.Create());
root.Subcommands.Add(ReadAnchorsCommand.Create());
root.Subcommands.Add(CheckAnchorBalanceCommand.Create());
root.Subcommands.Add(LegsCommand.Create());
root.Subcommands.Add(HostExecCommand.Create());
root.Subcommands.Add(HelpCommand.Create());

// Served on a host, for the DssHarness on the machine that reaches it. A run request goes back
// through this same parser, in the host's copy of the repository, which is why it is wired here.
root.Subcommands.Add(HostAgentCommand.Create(RunInAsync));

return await RunAsync(args, CancellationToken.None).ConfigureAwait(false);

async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
{
    var parseResult = root.Parse(arguments);

    // System.CommandLine reports a parse failure as exit code 1, which collides with
    // verify-git's contract, where 1 means "git is not installed". Usage errors are
    // reported with the documented shared code instead.
    if (parseResult.Errors.Count > 0)
    {
        foreach (var error in parseResult.Errors)
        {
            Console.Error.WriteLine(error.Message);
        }

        Console.Error.WriteLine("Run 'DssHarness --help' for usage.");
        return HarnessExit.UsageError;
    }

    // A deletion past its point of no return goes on after Ctrl+C, and a host agent can be running
    // one for another machine, so those two wait for their action as long as the deletion expects.
    // No other command is any slower to stop.
    var invocation = new InvocationConfiguration();

    if (parseResult.CommandResult.Command.Name is DeleteWorktreeCommand.Name or HostAgentProtocol.CommandName)
    {
        invocation.ProcessTerminationTimeout = WorktreeService.DefaultInterruptionGrace;
    }

    return await parseResult.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
}

// A command acts on the current directory when it is given no --directory, so a host runs a request
// in its copy of the repository by starting there, without its arguments being rewritten.
Task<int> RunInAsync(string directory, string[] arguments, CancellationToken cancellationToken)
{
    Directory.SetCurrentDirectory(directory);
    return RunAsync(arguments, cancellationToken);
}
