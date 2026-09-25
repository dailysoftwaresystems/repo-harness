using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Cli;

/// <summary>What a command body receives.</summary>
/// <param name="Services">The services for this invocation.</param>
/// <param name="ParseResult">The parsed command line, for the command's own arguments.</param>
/// <param name="Directory">
/// The directory to act on: absolute, and already confirmed to exist. Commands use this
/// rather than reading the option themselves, so none can act on a value that was not checked.
/// </param>
internal sealed record CommandContext(IServiceProvider Services, ParseResult ParseResult, string Directory)
{
    /// <summary>Resolves a service.</summary>
    internal T Get<T>()
        where T : notnull
        => Services.GetRequiredService<T>();
}

/// <summary>
/// Shared plumbing for every command: validate the global options, build services,
/// run the command's own orchestration, translate failures into exit codes, and
/// report the outcome. Keeping this in one place is what lets each command file
/// stay orchestration only, and is why every command refuses a bad directory the
/// same way rather than each discovering it differently.
/// </summary>
internal static class CommandRunner
{
    /// <summary>
    /// Whether the commands run from here on were asked for by the DssHarness on another machine,
    /// through this one's host agent.
    /// </summary>
    /// <remarks>
    /// Held for the flow of the request the agent serves, rather than for the process: a run request goes
    /// back through this same parser, in the host's copy, and nothing in its command line may say who
    /// asked for it without the command then answering to an argument its own user never typed.
    /// </remarks>
    private static readonly AsyncLocal<bool> Serving = new();

    /// <summary>
    /// Marks what this flow runs from here on as asked for by another machine. Set by the host agent
    /// before it serves a request; nothing that flow runs is then taken for something typed here.
    /// </summary>
    internal static void ServeAnotherMachine() => Serving.Value = true;

    /// <summary>Wraps a command body into an action the parser can invoke.</summary>
    /// <param name="commandName">The command, which prefixes every line it writes.</param>
    /// <param name="body">What the command does.</param>
    /// <param name="ledger">
    /// For a command that runs legs, its <c>--json</c>. Asked for its ledger as data, it answers with
    /// a ledger on every exit - one with no legs, when it stopped before any leg had a line: a leg
    /// nobody declared, a configuration that does not load, a runner nobody declared. A script
    /// reading standard output has one document to read whatever happened.
    /// </param>
    internal static Func<ParseResult, CancellationToken, Task<int>> Wrap(
        string commandName,
        Func<CommandContext, CancellationToken, Task<CommandOutcome>> body,
        Option<bool>? ledger = null)
    {
        return async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(GlobalOptions.Verbose);
            var prompting = !parseResult.GetValue(GlobalOptions.NoPrompt);
            await using var services = HarnessServices.Build(verbose, prompting, Serving.Value);
            var output = services.GetRequiredService<IHarnessOutput>();
            var answersWithLedger = ledger is not null && parseResult.GetValue(ledger);

            // The document is the whole of standard output from the first line, not from the moment
            // the legs are surveyed: a line written before then would sit in front of it.
            using var document = answersWithLedger ? output.DataOnly() : null;

            try
            {
                if (!TryResolveDirectory(parseResult, services, out var directory, out var problem))
                {
                    return Stop(output, commandName, HarnessExit.UsageError, problem, answersWithLedger);
                }

                var context = new CommandContext(services, parseResult, directory);
                var outcome = await body(context, cancellationToken).ConfigureAwait(false);

                Report(output, commandName, outcome);
                return outcome.ExitCode;
            }
            catch (Exception ex)
            {
                return Fail(output, commandName, ex, answersWithLedger);
            }
            finally
            {
                // Last, after each host's own refusal and the command's conclusion: the same for every host.
                services.GetRequiredService<SyncedCopyRefusals>().SayOnce(commandName);

                // However the command ended, a host it reached that asks to be held awake between commands is
                // held: the next command's own keepAwake ends the hold there. Asked apart from the command's
                // own token, so an interrupted command still leaves its holds.
                await services.GetRequiredService<HoldAwakeRegistry>().LeaveHoldsAsync(commandName, CancellationToken.None).ConfigureAwait(false);
            }
        };
    }

    /// <summary>
    /// Reports the exception that ended a command, and returns the exit code that says what it means.
    /// The one place a failure becomes an exit code, so a command a host serves for another machine
    /// fails exactly as a command typed here does.
    /// </summary>
    /// <param name="output">Where the command writes.</param>
    /// <param name="commandName">The command, which prefixes its failure line.</param>
    /// <param name="exception">What ended it.</param>
    /// <param name="ledger">Whether the command was asked for its ledger as data, which it then answers with.</param>
    internal static int Fail(IHarnessOutput output, string commandName, Exception exception, bool ledger = false)
    {
        var (exitCode, message, defect) = Meaning(exception);

        Stop(output, commandName, exitCode, message, ledger);

        // A defect's stack trace is there under --verbose, where someone is actually diagnosing it.
        if (defect && output.IsVerbose)
        {
            output.RawError(exception.ToString());
        }

        return exitCode;
    }

    /// <summary>
    /// What <paramref name="exception"/> means: the code the command exits with, the line it ends on,
    /// and whether it is a defect in this tool.
    /// </summary>
    private static (int ExitCode, string Message, bool Defect) Meaning(Exception exception)
    {
        // A cause both this and the leg executor know, read from the one table they share, so the
        // same missing program means the same thing whether it stopped a command or a leg.
        if (KnownCauses.ExitCodeFor(exception) is { } known)
        {
            return (known, exception.Message, false);
        }

        return exception switch
        {
            // The service already decided what this failure means.
            HarnessException harness => (harness.ExitCode, harness.Message, false),
            ConfigException => (HarnessExit.ConfigInvalid, exception.Message, false),

            // Not CommandFailed: nothing ran to completion, and a caller reading a failure code
            // would report a red verdict for an interrupted run.
            OperationCanceledException => (HarnessExit.Cancelled, "Interrupted before completion.", false),

            // A defect in the harness, not a failure of the thing being asked about. Reported as a
            // message with a defined exit code rather than an unhandled exception, whose exit code
            // would collide with a command's own contract.
            _ => (HarnessExit.InternalError, $"Unexpected {exception.GetType().Name}: {exception.Message}", true),
        };
    }

    /// <summary>
    /// Ends a command that did not succeed: with its ledger first, as the whole of standard output,
    /// when it was asked for one, and then the line that says why.
    /// </summary>
    private static int Stop(IHarnessOutput output, string commandName, int exitCode, string message, bool ledger)
    {
        if (ledger)
        {
            output.Data(LedgerReport.Stopped(exitCode, message));
        }

        output.Fail(commandName, message);
        return exitCode;
    }

    /// <summary>
    /// Resolves <c>--directory</c> to an absolute path and confirms it exists.
    /// </summary>
    /// <remarks>
    /// Done once, here. Without the existence check, the first service to start a child
    /// process in a missing directory fails with an error that names the wrong cause, and
    /// a relative path would reach every message as a fragment rather than the directory
    /// the user meant.
    /// </remarks>
    private static bool TryResolveDirectory(
        ParseResult parseResult,
        IServiceProvider services,
        out string directory,
        out string problem)
    {
        var requested = parseResult.GetValue(GlobalOptions.Directory) ?? System.Environment.CurrentDirectory;
        directory = string.Empty;
        problem = string.Empty;

        try
        {
            directory = Path.GetFullPath(requested);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"'{requested}' is not a usable directory path: {ex.Message}";
            return false;
        }

        if (!services.GetRequiredService<IFileSystem>().DirectoryExists(directory))
        {
            problem = $"Directory '{directory}' does not exist.";
            return false;
        }

        return true;
    }

    private static void Report(IHarnessOutput output, string commandName, CommandOutcome outcome)
    {
        foreach (var text in outcome.Data)
        {
            output.Data(text);
        }

        foreach (var detail in outcome.Details ?? [])
        {
            output.Info(commandName, detail);
        }

        if (!outcome.Succeeded)
        {
            output.Fail(commandName, outcome.Message);
        }
        else if (!outcome.Quiet)
        {
            output.Ok(commandName, outcome.Message);
        }
    }
}
