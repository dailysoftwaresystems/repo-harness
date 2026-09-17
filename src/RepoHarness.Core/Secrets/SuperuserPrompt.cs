using System.Text;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Core.Secrets;

/// <summary>Whether this run can ask a human for a password right now, and why not when it cannot.</summary>
public enum PromptAvailability
{
    /// <summary>
    /// Nobody can be asked: standard input is not a terminal, or the command is writing a document
    /// to standard output that a prompt would appear inside. Refused exactly as a run with no
    /// credential has always been, so a script sees the behaviour it already knows.
    /// </summary>
    Unavailable,

    /// <summary>A prompt can be shown, and what is typed will not be echoed.</summary>
    Available,

    /// <summary>
    /// A human is there and was not asked, because <c>--no-prompt</c> said not to. Told apart from
    /// <see cref="Unavailable"/> only so the refusal can name the flag that caused it.
    /// </summary>
    Suppressed,
}

/// <summary>
/// Asks whoever is running the command for one host's superuser password, when nothing else can
/// supply one.
/// </summary>
/// <remarks>
/// Declared here, beside the credential a host's own item can carry, because a human at a terminal is
/// the second source of exactly the same secret. The implementation that reads a terminal without
/// echoing it lives in the command line project, which is the only one that touches the console for
/// this: a host reached over ssh has no terminal of its own to read, and the machine that dispatched
/// the work is the only place a person is sitting.
/// </remarks>
public interface ISuperuserPrompt
{
    /// <summary>
    /// Whether anyone can be asked, measured now rather than when this was built: a command decides
    /// it is writing a document after the services it uses already exist.
    /// </summary>
    PromptAvailability Availability { get; }

    /// <summary>Whether anybody can be asked, from the three facts that decide it.</summary>
    /// <param name="prompting">Whether asking is allowed at all, which <c>--no-prompt</c> denies.</param>
    /// <param name="writingDocument">Whether a document is being written to standard output.</param>
    /// <param name="inputRedirected">Whether standard input is something other than a terminal.</param>
    /// <remarks>
    /// Whether anybody is there is settled before the flag is read, and not the other way round. A run
    /// with no terminal that passes <c>--no-prompt</c> anyway would otherwise be told that dropping the
    /// flag would have got it a prompt, and dropping it would refuse them again in the same words.
    /// </remarks>
    static PromptAvailability Decide(bool prompting, bool writingDocument, bool inputRedirected)
    {
        if (writingDocument || inputRedirected)
        {
            return PromptAvailability.Unavailable;
        }

        return prompting ? PromptAvailability.Available : PromptAvailability.Suppressed;
    }

    /// <summary>
    /// Reads one password for <paramref name="host"/>, echoing nothing. Called only while
    /// <see cref="Availability"/> is <see cref="PromptAvailability.Available"/>.
    /// </summary>
    /// <param name="host">The host the password unlocks, named in the prompt so two are never confused.</param>
    /// <param name="cancellationToken">
    /// Interrupts the wait. Nothing else bounds it: a person typing is not a host that stopped
    /// answering, and no budget here would measure anything but how fast they type.
    /// </param>
    /// <returns>What was typed, which may be empty.</returns>
    Task<string> ReadPasswordAsync(HostId host, CancellationToken cancellationToken);
}

/// <summary>Reading a secret from a keyboard, one key at a time, showing a mask instead of it.</summary>
/// <remarks>
/// Separated from the console it is normally read through so that it can be exercised without one.
/// A defect here does not look like a defect: a password one character short of what was typed is
/// indistinguishable from a wrong password, and the wrong password is then remembered for the whole
/// host.
/// </remarks>
public static class MaskedPassword
{
    /// <summary>Reads keys until Enter, and returns what they spelled.</summary>
    /// <param name="nextKey">The next key pressed, read without echoing it.</param>
    /// <param name="echo">Where the mask is drawn, which is never where the answer goes.</param>
    public static string Read(Func<ConsoleKeyInfo> nextKey, TextWriter echo)
    {
        ArgumentNullException.ThrowIfNull(nextKey);
        ArgumentNullException.ThrowIfNull(echo);

        var typed = new StringBuilder();

        while (true)
        {
            var key = nextKey();

            if (key.Key == ConsoleKey.Enter)
            {
                echo.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                // Nothing to take back is not an error, and must not take back the prompt itself.
                if (typed.Length > 0)
                {
                    typed.Length--;
                    echo.Write("\b \b");
                }

                continue;
            }

            // Arrow keys, function keys and the rest arrive carrying a control character for a key
            // that types nothing; taking those would put characters in a password nobody typed.
            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
                echo.Write('*');
            }
        }
    }
}
