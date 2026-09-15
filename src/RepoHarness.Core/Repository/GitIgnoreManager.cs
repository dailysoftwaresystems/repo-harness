using System.Text;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Repository;

/// <inheritdoc cref="IGitIgnoreManager"/>
public sealed class GitIgnoreManager(IFileSystem fileSystem) : IGitIgnoreManager
{
    /// <summary>Opening marker of the managed block.</summary>
    public const string BeginMarker = "# >>> repo-harness managed block >>>";

    /// <summary>Closing marker of the managed block.</summary>
    public const string EndMarker = "# <<< repo-harness managed block <<<";

    private readonly IFileSystem _fileSystem = fileSystem;

    public string ApplyManagedBlock(string? existingContent, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var newline = DetectNewline(existingContent);
        var block = BuildBlock(lines, newline);

        if (string.IsNullOrEmpty(existingContent))
        {
            return block;
        }

        // Each existing line keeps its own ending, including a trailing carriage return.
        // Rejoining them in one newline style would rewrite every line of a file whose
        // endings are mixed: the whole-file diff this method exists to avoid.
        var existingLines = existingContent.Split('\n');
        var kept = new List<string>(existingLines.Length);
        var insertAt = -1;

        // Lines after a begin marker are buffered rather than dropped outright. A marker
        // left unpaired by a hand edit or a merge would otherwise swallow everything after
        // it, and an end marker found later would pair with the wrong begin and delete the
        // user's rules in between. Both break the promise that hand written rules survive.
        List<string>? pending = null;

        foreach (var line in existingLines)
        {
            var trimmed = line.Trim();

            if (trimmed == BeginMarker)
            {
                // A second begin before any end means the first was unpaired: keep what it
                // appeared to enclose, and start buffering again.
                kept.AddRange(pending ?? []);
                insertAt = insertAt < 0 ? kept.Count : insertAt;
                pending = [];
                continue;
            }

            if (trimmed == EndMarker)
            {
                // Paired: everything buffered was the old block, so it is discarded. An end
                // with nothing buffered is a stray marker, and only the marker is dropped.
                insertAt = insertAt < 0 ? kept.Count : insertAt;
                pending = null;
                continue;
            }

            (pending ?? kept).Add(line);
        }

        // Reached the end still buffering: that begin marker had no end, so the lines it
        // appeared to enclose are the user's own and are kept.
        kept.AddRange(pending ?? []);

        var carriageReturn = newline == "\r\n" ? "\r" : string.Empty;
        var blockLines = block
            .Split(newline, StringSplitOptions.None)
            .SkipLast(1)
            .Select(line => line + carriageReturn)
            .ToList();

        if (insertAt >= 0)
        {
            var position = Math.Min(insertAt, kept.Count);
            var atEnd = position == kept.Count;

            // Splicing the block back where it was keeps a reviewer's diff limited to the
            // rules that actually changed.
            kept.InsertRange(position, blockLines);

            if (atEnd)
            {
                // The block is now the last thing in the file, so the file ends with it.
                kept.Add(string.Empty);
            }

            return string.Join('\n', kept);
        }

        var builder = new StringBuilder(string.Join('\n', kept));

        if (!existingContent.EndsWith('\n'))
        {
            builder.Append(newline);
        }

        builder.Append(newline).Append(block);
        return builder.ToString();
    }

    public bool Update(string path, IReadOnlyList<string> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var existing = _fileSystem.FileExists(path) ? _fileSystem.ReadAllText(path) : null;
        var updated = ApplyManagedBlock(existing, lines);

        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return false;
        }

        _fileSystem.WriteAllTextAtomic(path, updated);
        return true;
    }

    private static string BuildBlock(IReadOnlyList<string> lines, string newline)
    {
        var builder = new StringBuilder();
        builder.Append(BeginMarker).Append(newline);

        foreach (var line in lines)
        {
            builder.Append(line).Append(newline);
        }

        builder.Append(EndMarker).Append(newline);
        return builder.ToString();
    }

    /// <summary>
    /// Chooses the newline style for the managed block: CRLF when the file already uses
    /// it, so the block matches its surroundings. A file that does not exist yet is
    /// written with LF: .gitignore is tracked and shared between machines, so its line
    /// endings should not depend on which machine happened to run init.
    /// </summary>
    private static string DetectNewline(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return "\n";
        }

        return content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }
}
