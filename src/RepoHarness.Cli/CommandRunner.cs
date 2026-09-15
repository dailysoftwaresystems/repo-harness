using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
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
    /// <summary>Wraps a command body into an action the parser can invoke.</summary>
    internal static Func<ParseResult, CancellationToken, Task<int>> Wrap(
        string commandName,
        Func<CommandContext, CancellationToken, Task<CommandOutcome>> body)
    {
        return async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(GlobalOptions.Verbose);
            await using var services = HarnessServices.Build(verbose);
            var output = services.GetRequiredService<IHarnessOutput>();

            try
            {
                if (!TryResolveDirectory(parseResult, services, out var directory, out var problem))
                {
                    output.Fail(commandName, problem);
                    return HarnessExit.UsageError;
                }

                var context = new CommandContext(services, parseResult, directory);
                var outcome = await body(context, cancellationToken).ConfigureAwait(false);

                Report(output, commandName, outcome);
                return outcome.ExitCode;
            }
            catch (Exception ex)
            {
                return Fail(output, commandName, ex);
            }
        };
    }

    /// <summary>
    /// Reports the exception that ended a command, and returns the exit code that says what it means.
    /// The one place a failure becomes an exit code, so a command a host serves for another machine
    /// fails exactly as a command typed here does.
    /// </summary>
    internal static int Fail(IHarnessOutput output, string commandName, Exception exception)
    {
        switch (exception)
        {
            case HarnessException harness:
                // The service already decided what this failure means.
                output.Fail(commandName, harness.Message);
                return harness.ExitCode;

            case ConfigException:
                output.Fail(commandName, exception.Message);
                return HarnessExit.ConfigInvalid;

            case ExecutableNotFoundException:
                output.Fail(commandName, exception.Message);
                return HarnessExit.ToolMissing;

            case OperationCanceledException:
                // Not CommandFailed: nothing ran to completion, and a caller reading
                // a failure code would report a red verdict for an interrupted run.
                output.Fail(commandName, "Interrupted before completion.");
                return HarnessExit.Cancelled;

            default:
                // A defect in the harness, not a failure of the thing being asked
                // about. Reported as a message with a defined exit code rather than
                // an unhandled exception, whose exit code would collide with a
                // command's own contract. The stack trace is available under
                // --verbose, where someone is actually diagnosing it.
                output.Fail(commandName, $"Unexpected {exception.GetType().Name}: {exception.Message}");

                if (output.IsVerbose)
                {
                    output.RawError(exception.ToString());
                }

                return HarnessExit.InternalError;
        }
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
