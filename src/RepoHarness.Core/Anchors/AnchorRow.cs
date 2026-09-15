namespace RepoHarness.Core.Anchors;

/// <summary>One data row of a registry's anchor table, exactly as read.</summary>
/// <remarks>
/// A row keeps every piece its line split into rather than exactly six cells, so a malformed row
/// is still readable and linting can say what is wrong with it. The named cells read past the end
/// as empty.
/// </remarks>
public sealed class AnchorRow
{
    /// <summary>How many cells a well-formed row has.</summary>
    public const int ExpectedCellCount = 6;

    private readonly IReadOnlyList<string> _pieces;

    internal AnchorRow(int lineIndex, string rawLine, IReadOnlyList<string> pieces, string id)
    {
        LineIndex = lineIndex;
        RawLine = rawLine;
        _pieces = pieces;
        Id = id;
    }

    /// <summary>Zero-based index of the row's line in its file.</summary>
    public int LineIndex { get; }

    /// <summary>One-based line number, for reports.</summary>
    public int LineNumber => LineIndex + 1;

    /// <summary>The line as it appears in the file.</summary>
    public string RawLine { get; }

    /// <summary>The row's identity: the anchor id in its first cell.</summary>
    public string Id { get; }

    /// <summary>How many cells the line actually has.</summary>
    public int CellCount => Math.Max(0, _pieces.Count - 2);

    /// <summary>The Anchor cell's text, unescaped and untrimmed.</summary>
    public string AnchorCell => Cell(1);

    /// <summary>The Priority cell.</summary>
    public string Priority => Cell(2).Trim();

    /// <summary>The Status cell.</summary>
    public string Status => Cell(3).Trim();

    /// <summary>The Trigger cell.</summary>
    public string Trigger => Cell(4).Trim();

    /// <summary>The Closing work cell.</summary>
    public string ClosingWork => Cell(5).Trim();

    /// <summary>The Cross-refs cell.</summary>
    public string CrossRefs => Cell(6).Trim();

    /// <summary>Whether the Status cell reads closed.</summary>
    public bool IsClosed => AnchorStatus.IsClosed(Status);

    /// <summary>Cell <paramref name="position"/>, counting the Anchor cell as 1; empty past the end.</summary>
    public string Cell(int position) => position >= 1 && position < _pieces.Count ? _pieces[position] : string.Empty;
}
