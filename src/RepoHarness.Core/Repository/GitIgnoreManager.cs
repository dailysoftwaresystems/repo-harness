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

    public IReadOnlyList<GitIgnoreOverlap> FindOverlaps(string? content, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        // Each managed path has one direction: the block ignores a slot's contents and re-includes its
        // placeholder, and those normalise to two different paths.
        var managed = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var rule in lines.Select(PathOf))
        {
            if (rule is { } named)
            {
                managed[named.Path] = named.ReIncludes;
            }
        }

        var overlaps = new List<GitIgnoreOverlap>();
        var inside = false;
        var lineNumber = 0;

        foreach (var line in content.Split('\n'))
        {
            lineNumber++;
            var trimmed = line.Trim();

            // The same pairing ApplyManagedBlock writes: after an update there is exactly one block,
            // and everything between its markers is the harness's own.
            if (trimmed == BeginMarker)
            {
                inside = true;
                continue;
            }

            if (trimmed == EndMarker)
            {
                inside = false;
                continue;
            }

            if (inside || PathOf(line) is not { } rule || !managed.TryGetValue(rule.Path, out var managedReIncludes))
            {
                continue;
            }

            overlaps.Add(new GitIgnoreOverlap(
                lineNumber,
                line.TrimEnd('\r').Trim(),
                rule.Path,
                rule.ReIncludes,
                Contradicts: rule.ReIncludes != managedReIncludes));
        }

        return overlaps;
    }

    /// <summary>
    /// The path a rule names once the parts that only change its shape are dropped, or
    /// <see langword="null"/> for a blank line or a comment.
    /// </summary>
    /// <remarks>
    /// <c>/x/</c>, <c>/x/*</c>, <c>x/</c> and <c>!/x/*</c> all name <c>x</c>: they differ in what they
    /// match inside it and in which way, which is exactly the difference worth reporting when two of
    /// them meet. Trailing whitespace is dropped because git ignores it; leading whitespace is kept
    /// because git does not.
    /// </remarks>
    private static (string Path, bool ReIncludes)? PathOf(string rule)
    {
        var text = rule.TrimEnd('\r', ' ', '\t');

        if (text.Length == 0 || text.StartsWith('#'))
        {
            return null;
        }

        var reIncludes = text.StartsWith('!');
        text = reIncludes ? text[1..] : text;
        text = text.StartsWith('/') ? text[1..] : text;
        text = text.EndsWith("/*", StringComparison.Ordinal) ? text[..^2] : text;
        text = text.EndsWith('/') ? text[..^1] : text;

        return text.Length == 0 ? null : (text, reIncludes);
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
