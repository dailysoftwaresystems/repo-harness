using System.Buffers;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Which kind of shell an ssh server hands a command line to. It decides how the path to a
/// program has to be spelt, so it is measured once per connection rather than assumed.
/// </summary>
public enum RemoteShell
{
    /// <summary>
    /// A shell that leaves <c>%VAR%</c> alone and accepts forward slashes in a program's path: sh,
    /// bash, zsh and fish on any system, and PowerShell.
    /// </summary>
    Standard,

    /// <summary>cmd.exe, the default shell of OpenSSH on Windows, which needs backslashes.</summary>
    Cmd,
}

/// <summary>
/// Builds the one line an ssh server passes to its shell. Nothing is quoted. A token is refused
/// unless every character in it is one that sh, bash, zsh, fish, cmd and PowerShell all read
/// literally, so the shell receives exactly what was built, whichever shell it is. Anything that
/// cannot meet that, a path with a space or a user's arguments, travels on standard input instead.
/// </summary>
public static class RemoteCommandLine
{
    /// <summary>
    /// The command line that tells the shells apart: cmd expands <c>%COMSPEC%</c> to its own path, and
    /// every other shell prints it as written.
    /// </summary>
    public const string ShellProbe = "echo %COMSPEC%";

    /// <summary>Letters, digits, and punctuation no supported shell gives a meaning inside a word.</summary>
    private const string LiteralCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-/=:+";

    private static readonly SearchValues<char> Literal = SearchValues.Create(LiteralCharacters);

    /// <summary>The same, and the backslash cmd needs in a path, which a POSIX shell would consume.</summary>
    private static readonly SearchValues<char> CmdLiteral = SearchValues.Create(LiteralCharacters + "\\");

    /// <summary>Joins <paramref name="tokens"/> into a command line for <paramref name="shell"/>.</summary>
    /// <exception cref="ArgumentException">A token is empty, or holds a character a shell could reinterpret.</exception>
    public static string Join(IReadOnlyList<string> tokens, RemoteShell shell)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var literal = shell == RemoteShell.Cmd ? CmdLiteral : Literal;

        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token) || token.AsSpan().ContainsAnyExcept(literal))
            {
                throw new ArgumentException(
                    $"'{token}' cannot be passed to a remote shell without quoting; it has to travel on standard input.",
                    nameof(tokens));
            }
        }

        return string.Join(' ', tokens);
    }

    /// <summary>Reads which kind of shell answered <see cref="ShellProbe"/>.</summary>
    public static RemoteShell ReadShellProbe(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return output.Contains("%COMSPEC%", StringComparison.OrdinalIgnoreCase) ? RemoteShell.Standard : RemoteShell.Cmd;
    }
}
