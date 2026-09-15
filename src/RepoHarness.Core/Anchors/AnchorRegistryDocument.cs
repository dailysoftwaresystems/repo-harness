using System.Text.RegularExpressions;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>How serious a problem found in a registry is.</summary>
public enum AnchorFindingSeverity
{
    /// <summary>Rows exist that cannot be counted or placed: nothing can be trusted until it is fixed.</summary>
    Fatal,

    /// <summary>Every row was read, but the file will not render the way it reads.</summary>
    Warning,
}

/// <summary>A problem in the structure of a registry file.</summary>
/// <param name="LineNumber">One-based line the problem is at.</param>
/// <param name="Severity">Whether the problem stops the registry being trusted.</param>
/// <param name="Message">What is wrong, for a person.</param>
public sealed record AnchorDocumentFinding(int LineNumber, AnchorFindingSeverity Severity, string Message);

/// <summary>A registry file: its prose, its single anchor table, and the rows in it.</summary>
/// <remarks>
/// Tables are found by their header row, never by the heading above them. Rows are read from the
/// table, not matched line by line, so a row that has drifted out of its table would go unread; any
/// line that looks like an anchor row but belongs to no anchor table is therefore reported rather
/// than silently skipped. The file's own line endings are kept on every line it writes.
/// </remarks>
public sealed partial class AnchorRegistryDocument
{
    /// <summary>The header row every anchor table starts with.</summary>
    public const string TableHeader = "| Anchor | Priority | Status | Trigger | Closing work | Cross-refs |";

    /// <summary>The separator row written under a new table's header.</summary>
    public const string SeparatorRow = "|---|---|---|---|---|---|";

    private const int ExcerptLength = 100;

    private readonly List<string> _lines;
    private readonly List<(int HeaderIndex, int EndIndex)> _tables;
    private bool _modified;

    private AnchorRegistryDocument(
        List<string> lines,
        List<(int HeaderIndex, int EndIndex)> tables,
        IReadOnlyList<AnchorRow> rows,
        IReadOnlyList<AnchorDocumentFinding> findings)
    {
        _lines = lines;
        _tables = tables;
        Rows = rows;
        Findings = findings;
    }

    /// <summary>Every row of every anchor table, in file order.</summary>
    public IReadOnlyList<AnchorRow> Rows { get; }

    /// <summary>Structural problems, including a missing or duplicated anchor table.</summary>
    public IReadOnlyList<AnchorDocumentFinding> Findings { get; }

    /// <summary>Reads a registry file's text.</summary>
    public static AnchorRegistryDocument Parse(string text, AnchorIdRules rules)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rules);

        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = text.Split('\n').ToList();
        var tables = new List<(int HeaderIndex, int EndIndex)>();
        var rows = new List<AnchorRow>();
        var findings = new List<AnchorDocumentFinding>();
        var consumed = new HashSet<int>();

        var index = 0;
        while (index < lines.Count)
        {
            if (!IsTableLine(lines[index]) || index + 1 >= lines.Count || !Separator().IsMatch(lines[index + 1]))
            {
                index++;
                continue;
            }

            consumed.Add(index);
            consumed.Add(index + 1);

            var body = new List<int>();
            var end = ReadBody(lines, index + 2, body, consumed, findings);

            if (string.Equals(lines[index].Trim(), TableHeader, StringComparison.Ordinal))
            {
                tables.Add((index, end));

                foreach (var line in body)
                {
                    var pieces = AnchorCells.Split(lines[line]);
                    rows.Add(new AnchorRow(line, lines[line], pieces, pieces.Count > 1 ? rules.Identify(pieces[1]) : "<blank>"));
                }
            }
            else if (body.Any(line => HoldsAnchorId(lines[line], rules)))
            {
                // Another table holding anchor ids is where rows go to be missed: nothing counts
                // them, and nothing says so.
                findings.Add(new AnchorDocumentFinding(
                    index + 1,
                    AnchorFindingSeverity.Fatal,
                    "a table that is not the anchor table holds anchor rows, and they are counted by nothing: "
                    + AnchorCells.Excerpt(lines[index], ExcerptLength)));
            }

            index = end;
        }

        for (var line = 0; line < lines.Count; line++)
        {
            if (!consumed.Contains(line) && IsTableLine(lines[line]) && HoldsAnchorId(lines[line], rules))
            {
                findings.Add(new AnchorDocumentFinding(
                    line + 1,
                    AnchorFindingSeverity.Fatal,
                    "an anchor row sits outside any table, so nothing reads it: "
                    + AnchorCells.Excerpt(lines[line], ExcerptLength)));
            }
        }

        if (tables.Count == 0)
        {
            findings.Add(new AnchorDocumentFinding(
                1,
                AnchorFindingSeverity.Fatal,
                $"no anchor table: expected the header '{TableHeader}' followed by a separator row"));
        }
        else if (tables.Count > 1)
        {
            findings.Add(new AnchorDocumentFinding(
                tables[1].HeaderIndex + 1,
                AnchorFindingSeverity.Fatal,
                $"{tables.Count} anchor tables; a registry holds exactly one, or where a new row belongs would be a guess"));
        }

        return new AnchorRegistryDocument(lines, tables, rows, findings.OrderBy(finding => finding.LineNumber).ToList());
    }

    /// <summary>
    /// Refuses a file with any fatal finding: no anchor table, a second one, or an anchor row outside the
    /// anchor table. Read around, such a file could miscount an anchor or give it a second row, and
    /// nobody would be told.
    /// </summary>
    /// <exception cref="HarnessException">The file has a fatal finding.</exception>
    public void EnsureSound(string displayPath)
    {
        var fatal = Findings.Where(finding => finding.Severity == AnchorFindingSeverity.Fatal).ToList();

        if (fatal.Count == 0)
        {
            return;
        }

        var lines = new List<string>
        {
            $"'{displayPath}' is malformed, so no anchor is read from it or written to it until it is repaired:",
        };

        lines.AddRange(fatal.Select(finding => $"  line {finding.LineNumber}: {finding.Message}"));
        lines.Add("'DssHarness read-anchors --lint' lists every problem in both registries.");

        throw new HarnessException(HarnessExit.CommandFailed, string.Join(Environment.NewLine, lines));
    }

    /// <summary>The file's text, with any change made to it.</summary>
    public string ToText() => string.Join('\n', _lines);

    /// <summary>Appends a row at the end of the anchor table. Rows are never sorted.</summary>
    internal void AppendRow(string row)
    {
        BeginChange();

        var (header, end) = _tables[0];
        _lines.Insert(end, row + LineEndingOf(_lines[header]));
    }

    /// <summary>Replaces a row's line, keeping that line's own ending.</summary>
    internal void ReplaceRow(AnchorRow existing, string row)
    {
        BeginChange();
        _lines[existing.LineIndex] = row + LineEndingOf(existing.RawLine);
    }

    /// <summary>Removes a row's line.</summary>
    internal void RemoveRow(AnchorRow existing)
    {
        BeginChange();
        _lines.RemoveAt(existing.LineIndex);
    }

    /// <summary>
    /// Allows one change per document. Row positions are read when the file is parsed, and a second
    /// change would apply to positions the first one has already moved.
    /// </summary>
    private void BeginChange()
    {
        if (_modified)
        {
            throw new InvalidOperationException("A registry document takes one change; parse the file again for another.");
        }

        _modified = true;
    }

    /// <summary>
    /// Reads a table's body from <paramref name="start"/>, returning the index just past its last row.
    /// A whole-line HTML comment between rows is stepped over and reported: a markdown renderer ends the
    /// table there, but every row below it is still a row, and dropping them would undercount.
    /// </summary>
    private static int ReadBody(
        List<string> lines,
        int start,
        List<int> body,
        HashSet<int> consumed,
        List<AnchorDocumentFinding> findings)
    {
        var index = start;

        while (index < lines.Count)
        {
            if (IsTableLine(lines[index]))
            {
                body.Add(index);
                consumed.Add(index);
                index++;
                continue;
            }

            if (CommentOpen().IsMatch(lines[index]))
            {
                var close = index;
                while (close < lines.Count && !CommentClose().IsMatch(lines[close]))
                {
                    close++;
                }

                if (close + 1 < lines.Count && IsTableLine(lines[close + 1]))
                {
                    for (var line = index; line <= close; line++)
                    {
                        consumed.Add(line);
                    }

                    findings.Add(new AnchorDocumentFinding(
                        index + 1,
                        AnchorFindingSeverity.Warning,
                        "an HTML comment interrupts the table: the rows below it were read, but a markdown renderer shows them outside the table"));

                    index = close + 1;
                    continue;
                }
            }

            break;
        }

        return index;
    }

    private static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

    private static bool HoldsAnchorId(string line, AnchorIdRules rules)
    {
        var pieces = AnchorCells.Split(line);
        return pieces.Count > 2 && rules.ContainsId(pieces[1]);
    }

    private static string LineEndingOf(string line) => line.EndsWith('\r') ? "\r" : string.Empty;

    [GeneratedRegex(@"^\s*\|[\s:|-]+\|\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();

    [GeneratedRegex(@"^\s*<!--", RegexOptions.CultureInvariant)]
    private static partial Regex CommentOpen();

    [GeneratedRegex(@"-->\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CommentClose();
}
