using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>One cell as the command line gave it: inline, in a file, or not at all.</summary>
/// <param name="Cell">The column, named in a refusal.</param>
/// <param name="InlineOption">The option carrying the text itself, for the refusal's wording.</param>
/// <param name="FileOption">The option carrying a file to read it from.</param>
/// <param name="Inline">The inline value, or null when the option was not given.</param>
/// <param name="File">The file's path, or null when that option was not given.</param>
public sealed record AnchorCellInput(
    string Cell,
    string InlineOption,
    string FileOption,
    string? Inline,
    string? File);

/// <summary>Resolves a cell to its text, whether it came from the command line or from a file.</summary>
/// <remarks>
/// A cell can be given as a file because a command line is bounded and a row is not. Windows caps one
/// at 32,767 characters, and the longest row measured in the repository these commands serve is 78 KB:
/// without a file to read it from, that row cannot be written by the tool meant to maintain it, and
/// whoever maintains it goes back to editing the table by hand, which is the failure the table's own
/// escaping rules exist to prevent.
/// </remarks>
public static class AnchorCellInputs
{
    /// <summary>
    /// The cell's text, or <see langword="null"/> when neither option was given, which every command
    /// reads as "leave this cell alone".
    /// </summary>
    /// <param name="fileSystem">Reads the file, when one was named.</param>
    /// <param name="input">The two options for one cell, as they were given.</param>
    /// <exception cref="HarnessException">
    /// Both options were given, so which text the row should carry is not decided; or the file named
    /// is not there.
    /// </exception>
    public static string? Resolve(IFileSystem fileSystem, AnchorCellInput input)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(input);

        if (input.Inline is not null && input.File is not null)
        {
            // Refused rather than ranked. A precedence rule would silently discard one of two values
            // the caller meant, and which one was dropped is invisible in the row that results.
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{input.InlineOption} and {input.FileOption} both give the {input.Cell} cell. Give one of them.");
        }

        if (input.File is null)
        {
            return input.Inline;
        }

        if (!fileSystem.FileExists(input.File))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{input.FileOption} names '{input.File}', which is not a file, so the {input.Cell} cell has no text.");
        }

        return TrimOneLineEnding(fileSystem.ReadAllText(input.File));
    }

    /// <summary>
    /// Drops the single line ending a file ends with, and nothing else. Every editor writes one and no
    /// author means it as part of the value; dropping more would quietly eat a deliberate blank line
    /// at the end of a long cell.
    /// </summary>
    private static string TrimOneLineEnding(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }

        return text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r') ? text[..^1] : text;
    }
}
