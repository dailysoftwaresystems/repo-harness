using System.Text.RegularExpressions;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>Reads and writes the cells of a registry table row.</summary>
/// <remarks>
/// A row is one physical line of a markdown table, and every way of producing one by hand fails
/// quietly: a raw <c>|</c> inside a cell adds a column, so every later cell shifts and the status is
/// read from the wrong place; a line break wraps the row, and its id disappears from every search.
/// Values therefore go through <see cref="Format"/>, which makes neither expressible.
/// </remarks>
public static partial class AnchorCells
{
    /// <summary>
    /// Splits a table line on its unescaped pipes. Element 0 is whatever precedes the first pipe, so
    /// the first cell is element 1, and an escaped <c>\|</c> comes back as a plain pipe.
    /// </summary>
    public static IReadOnlyList<string> Split(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return [.. UnescapedPipe().Split(line).Select(piece => piece.Replace(@"\|", "|", StringComparison.Ordinal))];
    }

    /// <summary>
    /// The cells between the line's unescaped pipes exactly as the line spells them, escapes and
    /// padding included, so a cell nobody asked to change can be written back byte for byte.
    /// </summary>
    public static IReadOnlyList<string> RawCells(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var pipes = UnescapedPipe().Matches(line);
        var cells = new List<string>(Math.Max(0, pipes.Count - 1));

        for (var index = 0; index + 1 < pipes.Count; index++)
        {
            cells.Add(line[(pipes[index].Index + 1)..pipes[index + 1].Index]);
        }

        return cells;
    }

    /// <summary>
    /// Turns a value into a cell: whitespace, line breaks included, collapses to single spaces, every
    /// pipe is escaped, and the result is padded the way the table's own rows are.
    /// </summary>
    /// <param name="text">The value exactly as its author means it, pipes as plain pipes.</param>
    /// <param name="field">The column, named in the refusal.</param>
    /// <exception cref="HarnessException">
    /// The value already contains an escaped pipe. Escaping it again would store the backslash twice
    /// and show a stray one to every reader; see the message for the two ways that happens.
    /// </exception>
    public static string Format(string? text, string field)
    {
        var flat = Collapse(text ?? string.Empty);
        var escaped = flat.IndexOf(@"\|", StringComparison.Ordinal);

        if (escaped >= 0)
        {
            var from = Math.Max(0, escaped - 40);
            var to = Math.Min(flat.Length, escaped + 40);

            throw new HarnessException(
                HarnessExit.UsageError,
                $"The {field} value contains a backslash immediately before a pipe. Pipes are escaped "
                + "for you, so a pipe escaped by hand would be stored with two backslashes and show a "
                + "stray one to every reader. If you escaped it yourself, write the plain pipe. If you "
                + "copied the text from the raw table line, take it from 'repo-harness read-anchor "
                + "--json' instead, whose fields come back unescaped. To show a backslash before a pipe "
                + $"on purpose, write the pipe as [|]. At character {escaped}: ...{flat[from..to]}...");
        }

        return flat.Length == 0 ? " " : " " + flat.Replace("|", @"\|", StringComparison.Ordinal) + " ";
    }

    /// <summary>Drops emphasis, code ticks and strikethrough, and collapses whitespace. Keeps case.</summary>
    public static string StripDecoration(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Collapse(text
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("~", string.Empty, StringComparison.Ordinal));
    }

    /// <summary>Collapses every run of whitespace, line breaks included, to one space, and trims.</summary>
    public static string Collapse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Shortens <paramref name="text"/> for a report, collapsed to one line.</summary>
    public static string Excerpt(string text, int length)
    {
        var flat = Collapse(text);
        return flat.Length <= length ? flat : flat[..length];
    }

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedPipe();
}
