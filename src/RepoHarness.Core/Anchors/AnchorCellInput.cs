using System.Text;
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
    /// is not there, cannot be read, is not UTF-8, or opens with a byte-order mark.
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

        return Decode(fileSystem, input.File, input);
    }

    /// <summary>A decoder that raises on bytes that do not form a UTF-8 character, rather than writing U+FFFD in their place.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The file's text, read as UTF-8 and nothing else: a file that is not, or that opens with a byte-order
    /// mark, is refused by name rather than cleaned. The line ending a file ends with is the value's own
    /// last line break, and goes as every line break does.
    /// </summary>
    /// <remarks>
    /// Read leniently, a Latin-1 byte was stored as U+FFFD, and the character its author wrote was gone with
    /// nothing said; a byte-order mark was dropped, or kept as an invisible first character of the cell,
    /// depending on the reader.
    /// </remarks>
    private static string Decode(IFileSystem fileSystem, string path, AnchorCellInput input)
    {
        byte[] bytes;

        try
        {
            using var stream = fileSystem.OpenRead(path);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Named, as a file that is not there is: one another program holds, one this user may not read, or one
            // removed since it was found are each the file the option named, and nothing wrong with this tool.
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{input.FileOption} names '{path}', which could not be read: {ex.Message.TrimEnd('.')}. The {input.Cell} "
                + "cell has no text until it can be.",
                ex);
        }

        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{input.FileOption} names '{path}', which opens with a byte-order mark, so the {input.Cell} cell would "
                + "open with an invisible character. Save the file as UTF-8 without one.");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{input.FileOption} names '{path}', which is not UTF-8: the byte at offset {ex.Index} "
                + $"(0x{ex.BytesUnknown?.FirstOrDefault() ?? 0:X2}) does not form a UTF-8 character, so the {input.Cell} cell "
                + "cannot be read as written. Save the file as UTF-8.",
                ex);
        }
    }
}
