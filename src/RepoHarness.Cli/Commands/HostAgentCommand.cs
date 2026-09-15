using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;

namespace RepoHarness.Cli.Commands;

/// <summary>
/// Wires the hidden <c>repo-harness host-agent</c>, through which the repo-harness on another machine
/// asks this one what it is, or has it run a command. Hidden because nobody types it: <c>legs</c> and
/// <c>host-exec</c> start it over wsl.exe or ssh, with the request on standard input.
/// </summary>
internal static class HostAgentCommand
{
    /// <summary>Builds the command.</summary>
    /// <param name="run">Runs a command line in a directory; supplied by the program, which holds the parser.</param>
    internal static Command Create(Func<string, string[], Task<int>> run)
    {
        var command = new Command(
            HostAgentProtocol.CommandName,
            "Serve one request from the repo-harness on another machine, read as JSON from standard input.")
        {
            Hidden = true,
        };

        command.SetAction(async (_, cancellationToken) =>
        {
            await using var services = HarnessServices.Build(verbose: false);

            // The request was written as UTF-8, and is read as such whatever the console's own input
            // encoding happens to be.
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            try
            {
                return await services.GetRequiredService<HostAgentService>()
                    .ServeAsync(input, Console.Out, Console.Error, run, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Reported, and turned into an exit code, exactly as any other command's failure is. Left to
                // the parser, a defect would exit 1 with a stack trace, and the machine that asked would quote
                // frames instead of the reason.
                return CommandRunner.Fail(services.GetRequiredService<IHarnessOutput>(), HostAgentProtocol.CommandName, ex);
            }
        });

        return command;
    }
}
