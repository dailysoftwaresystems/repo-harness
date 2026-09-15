using System.Text.RegularExpressions;

namespace RepoHarness.Core.Hosts;

/// <summary>What ssh applies to one host from a configuration file, of what the harness checks before connecting.</summary>
/// <param name="IdentityFiles">Every key the file gives the host, as written, in the order ssh reads them.</param>
public sealed record SshHostEntry(IReadOnlyList<string> IdentityFiles);

/// <summary>
/// Reads the parts of an ssh configuration file the harness checks before it connects. A host counts as
/// declared only when a <c>Host</c> line names it exactly: a wildcard entry alone would let a misspelt
/// name in config.json connect to whatever machine the pattern happened to match. What applies to the
/// host is gathered the way ssh gathers it: from the lines before the first block, and from every
/// <c>Host</c> block whose patterns select it, <c>Host *</c> included.
/// </summary>
/// <remarks>
/// <c>Match</c> blocks and <c>Include</c> are not evaluated, so a key named only there is not checked
/// here; ssh still refuses an unprotected key itself when it connects.
/// </remarks>
public static class SshConfigFile
{
    /// <summary>What applies to <paramref name="name"/>, or <see langword="null"/> when no <c>Host</c> line names it.</summary>
    public static SshHostEntry? Find(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var declared = false;

        // Lines before the first Host or Match line apply to every host.
        var applies = true;
        var identityFiles = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var (keyword, value) = Split(line);

            if (keyword.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                var patterns = value
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Unquote)
                    .ToList();

                declared |= patterns.Any(pattern => pattern.Equals(name, StringComparison.OrdinalIgnoreCase));
                applies = Selects(patterns, name);
            }
            else if (keyword.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                // A Match block applies by conditions this reader does not evaluate.
                applies = false;
            }
            else if (applies && keyword.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase))
            {
                identityFiles.Add(Unquote(value));
            }
        }

        return declared ? new SshHostEntry(identityFiles) : null;
    }

    /// <summary>
    /// Whether a <c>Host</c> line's patterns select <paramref name="name"/>, as ssh decides: a negated pattern
    /// that matches rules the host out, and otherwise any pattern that matches rules it in.
    /// </summary>
    private static bool Selects(IReadOnlyList<string> patterns, string name)
    {
        var selected = false;

        foreach (var pattern in patterns)
        {
            var negated = pattern.StartsWith('!');

            if (!Matches(negated ? pattern[1..] : pattern, name))
            {
                continue;
            }

            if (negated)
            {
                return false;
            }

            selected = true;
        }

        return selected;
    }

    /// <summary>ssh's wildcards: <c>*</c> for any run of characters and <c>?</c> for exactly one, ignoring case.</summary>
    private static bool Matches(string pattern, string name)
        => Regex.IsMatch(
            name,
            "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    /// <summary>Splits a line into its keyword and its value, which ssh separates with spaces or an equals sign.</summary>
    private static (string Keyword, string Value) Split(string line)
    {
        var end = line.IndexOfAny([' ', '\t', '=']);

        return end < 0
            ? (line, string.Empty)
            : (line[..end], line[end..].TrimStart(' ', '\t', '=').Trim());
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
