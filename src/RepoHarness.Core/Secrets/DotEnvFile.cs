namespace RepoHarness.Core.Secrets;

/// <summary>
/// Reads a file of <c>NAME=value</c> lines. Names compare exactly, case included, as an environment
/// name does on every system but Windows: a reader that accepted <c>address</c> for <c>ADDRESS</c>
/// would also accept it for a second, different entry, and the file would then mean two things.
/// A refusal names the key it wanted, so an exact match costs nothing to satisfy.
/// </summary>
/// <remarks>
/// A blank line and a line whose first non-blank character is <c>#</c> are skipped, as is a line with
/// no <c>=</c>. Surrounding whitespace is removed from the name and from the value, and one matching
/// pair of surrounding quotes is removed from the value, so a value whose own spaces matter can be
/// written with them: a credential ending in a space is otherwise silently shortened, and the login
/// it fails at says only that the password was wrong.
/// </remarks>
public static class DotEnvFile
{
    /// <summary>Reads <paramref name="text"/> into its names and values.</summary>
    public static IReadOnlyDictionary<string, string> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            // The trailing carriage return of a file written on Windows is part of no value: a port
            // read as "22\r" is not a number, and an address read that way resolves nowhere.
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();

            if (name.Length > 0)
            {
                values[name] = Unquote(line[(separator + 1)..].Trim());
            }
        }

        return values;
    }

    /// <summary>
    /// Removes one matching pair of surrounding quotes, and nothing else.
    /// </summary>
    /// <param name="value">The text after the <c>=</c>, already trimmed.</param>
    /// <remarks>
    /// Shared with every other reader of this shape, so two files written the same way cannot be
    /// read two ways. One pair, not all of them: a value that is meant to carry a quote keeps it.
    /// </remarks>
    public static string Unquote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;
    }
}
