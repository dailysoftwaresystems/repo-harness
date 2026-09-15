using System.CommandLine;
using RepoHarness.Cli;
using RepoHarness.Cli.Commands;
using RepoHarness.Core.Results;

// Command selection and dependency wiring only. Every behaviour lives in a service
// in RepoHarness.Core, which is a library precisely so that nothing can accumulate here.
using var consoleEncoding = ConsoleEncoding.UseUtf8();

var root = new RootCommand(
    "repo-harness - cross-platform repository harness. Run 'repo-harness help' for reference material.");

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
root.Subcommands.Add(HelpCommand.Create());

var parseResult = root.Parse(args);

// System.CommandLine reports a parse failure as exit code 1, which collides with
// verify-git's contract, where 1 means "git is not installed". Usage errors are
// reported with the documented shared code instead.
if (parseResult.Errors.Count > 0)
{
    foreach (var error in parseResult.Errors)
    {
        Console.Error.WriteLine(error.Message);
    }

    Console.Error.WriteLine("Run 'repo-harness --help' for usage.");
    return HarnessExit.UsageError;
}

return await parseResult.InvokeAsync().ConfigureAwait(false);
