using System.CommandLine;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness host-exec</c>.</summary>
internal static class HostExecCommand
{
    internal const string Name = HostExecService.CommandName;

    private static readonly Option<string?> SshOption = new("--ssh")
    {
        Description = "Run on this ssh host, declared under hosts.ssh.",
    };

    private static readonly Option<string?> WslOption = new("--wsl")
    {
        Description = "Run in this WSL distribution, declared under hosts.wsl; with no name, WSL's default distribution.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Argument<string[]> CommandArgument = new("command")
    {
        Description = "The DssHarness command to run there, with its arguments. Put -- before it, so its options stay its own.",
        Arity = ArgumentArity.OneOrMore,
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Run a DssHarness command on a WSL distribution or an ssh host, in that host's copy of the repository, installing or updating DssHarness there first when it is behind.");

        command.Options.Add(SshOption);
        command.Options.Add(WslOption);
        command.Arguments.Add(CommandArgument);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            // --wsl on its own means the default distribution, which is told apart from no --wsl at all
            // by whether the option was given.
            var wsl = arguments.GetResult(WslOption) is { Implicit: false }
                ? arguments.GetValue(WslOption) ?? string.Empty
                : null;

            return context.Get<HostExecService>().RunAsync(
                context.Directory,
                arguments.GetValue(SshOption),
                wsl,
                arguments.GetValue(CommandArgument) ?? [],
                cancellationToken);
        }));

        return command;
    }
}
