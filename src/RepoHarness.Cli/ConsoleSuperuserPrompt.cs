using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Secrets;

namespace RepoHarness.Cli;

/// <summary>
/// Reads a superuser password from the console, masked, and only when there is a console to read.
/// </summary>
/// <remarks>
/// The one place this tool reads its own standard input for a person's answer. Everything that
/// decides whether a password is wanted, which host it belongs to, and what happens when it is
/// refused lives in the core; so does what the keys spell and when nobody may be asked. What is left
/// here is the console itself, which is the only part no test can stand in for.
/// </remarks>
/// <param name="output">Asked whether a document is being written, which a prompt must not appear in.</param>
/// <param name="prompting">Whether asking is allowed at all, which <c>--no-prompt</c> denies.</param>
internal sealed class ConsoleSuperuserPrompt(IHarnessOutput output, bool prompting) : ISuperuserPrompt
{
    private readonly IHarnessOutput _output = output;

    public PromptAvailability Availability
        // Both facts are measured now rather than when this was built: a command opens its document
        // after the services it uses exist, and standard input belongs to the process, not to this.
        => ISuperuserPrompt.Decide(prompting, _output.IsDataOnly, Console.IsInputRedirected);

    public async Task<string> ReadPasswordAsync(HostId host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);

        // Standard error, not standard output: a run piped into something still has a terminal on its
        // input, and a prompt written into that pipe instead of onto the terminal is a wait with no
        // visible cause. This is the same reason a failure is reported there.
        Console.Error.Write($"[sudo] password for {host}: ");

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Console.ReadKey blocks the thread that calls it and takes no token, so the read runs on
            // one of its own and this waits on whichever finishes first. Interrupted, the abandoned
            // read holds a thread that ends with the process; waiting on it here instead would ignore
            // Ctrl+C until somebody pressed one more key.
            var read = Task.Run(
                () => MaskedPassword.Read(() => Console.ReadKey(intercept: true), Console.Error),
                CancellationToken.None);

            var stopped = Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token);

            if (await Task.WhenAny(read, stopped).ConfigureAwait(false) != read)
            {
                // The line was left mid-prompt, and what follows should not continue it.
                Console.Error.WriteLine();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await read.ConfigureAwait(false);
        }
        finally
        {
            // Ends the wait whichever way this finished, so nothing is left registered on the run's
            // own token for as long as the command lasts.
            await stopping.CancelAsync().ConfigureAwait(false);
        }
    }
}
