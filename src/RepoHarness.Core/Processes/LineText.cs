namespace RepoHarness.Core.Processes;

/// <summary>Text as its lines read, each ended by a line feed alone, whatever ended it where it was written.</summary>
/// <remarks>
/// A pattern anchored with <c>$</c> in multiline mode matches before a line feed, and a Windows program ends its
/// lines with a carriage return and a line feed: the carriage return is left between the text and the end, and
/// a pattern that passes on Linux and macOS never matches on Windows. A consumer's manual step exited 0 on a
/// Windows leg having printed exactly the line its pattern described, and was unwitnessed, while the log - which
/// holds each line without its terminator - showed a line the pattern matched. Matched as the log keeps it, a
/// pattern means the same on every platform.
/// </remarks>
public static class LineText
{
    /// <summary>
    /// <paramref name="text"/> with each carriage return that ends a line removed - one before a line feed, or at
    /// the very end - as the process runner removes it from each line it hands on.
    /// </summary>
    /// <param name="text">What a program wrote, or a file holds.</param>
    public static string Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!text.Contains('\r', StringComparison.Ordinal))
        {
            return text;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal);

        return lines.EndsWith('\r') ? lines[..^1] : lines;
    }
}
