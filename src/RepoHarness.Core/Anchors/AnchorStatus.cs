namespace RepoHarness.Core.Anchors;

/// <summary>The states an anchor can be in.</summary>
public enum AnchorState
{
    /// <summary>Live work that can be picked up now.</summary>
    Open,

    /// <summary>Live work waiting on a trigger.</summary>
    Gated,

    /// <summary>Live debt that existed before anyone wrote it down.</summary>
    Disclosed,

    /// <summary>Finished.</summary>
    Closed,
}

/// <summary>
/// How a status is spelled in a registry, and the one test that decides whether a row is closed.
/// </summary>
/// <remarks>
/// The Status cell is the only verdict that decides whether a row is closed: nothing is inferred from the prose
/// beside it, even where a registry states the verdict in its Trigger too.
/// Each status is written glyph first and word second. The glyph is what the closed test reads,
/// because a test on the first character of the cell has one answer however the rest is phrased;
/// the word is there for the person reading the table.
/// </remarks>
public static class AnchorStatus
{
    /// <summary>The glyph that opens a closed Status cell.</summary>
    public const string ClosedMark = "✅";

    /// <summary>The glyph that opens a disclosed Status cell.</summary>
    public const string DisclosedMark = "🔵";

    private static readonly (AnchorState State, string Word, string Cell)[] Spellings =
    [
        (AnchorState.Open, "open", "🟠 OPEN"),
        (AnchorState.Gated, "gated", "⏳ GATED"),
        (AnchorState.Disclosed, "disclosed", "🔵 DISCLOSED"),
        (AnchorState.Closed, "closed", ClosedMark + " CLOSED"),
    ];

    /// <summary>The words a command accepts for a status.</summary>
    public static IReadOnlyList<string> Words { get; } = [.. Spellings.Select(spelling => spelling.Word)];

    /// <summary>Every status exactly as a registry spells it.</summary>
    public static IReadOnlyList<string> Cells { get; } = [.. Spellings.Select(spelling => spelling.Cell)];

    /// <summary>The Status cell for <paramref name="state"/>.</summary>
    public static string Render(AnchorState state) => Spellings.First(spelling => spelling.State == state).Cell;

    /// <summary>
    /// Reads a status given as its word, in any case, or exactly as a registry spells it. Nothing
    /// else is accepted: a synonym is one more spelling every reader has to know.
    /// </summary>
    public static bool TryParse(string? value, out AnchorState state)
    {
        var text = (value ?? string.Empty).Trim();
        var word = text.TrimStart('*', '_', ' ').ToLowerInvariant();

        foreach (var spelling in Spellings)
        {
            if (word == spelling.Word || text == spelling.Cell)
            {
                state = spelling.State;
                return true;
            }
        }

        state = default;
        return false;
    }

    /// <summary>
    /// How a Status cell and a Trigger cell state different verdicts - one reads closed, as
    /// <see cref="IsClosed"/> reads a cell, and the other does not - or <see langword="null"/> where they agree.
    /// </summary>
    /// <param name="status">The Status cell.</param>
    /// <param name="trigger">The Trigger cell.</param>
    public static string? SplitVerdict(string status, string trigger)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(trigger);

        var closed = IsClosed(status);

        if (closed == IsClosed(trigger))
        {
            return null;
        }

        return closed
            ? $"the Status reads closed, and the Trigger does not open with the closed mark, {ClosedMark}"
            : $"the Trigger opens with the closed mark, {ClosedMark}, and the Status does not read closed";
    }

    /// <summary>Whether a Status cell reads closed: it opens with the closed mark, ignoring emphasis.</summary>
    /// <remarks>
    /// The closed spelling is defined and everything else is open, so a glyph nobody anticipated
    /// reads as open. A row wrongly read as open stays visible as work; a row wrongly read as
    /// closed disappears from every count.
    /// </remarks>
    public static bool IsClosed(string cell) => Lead(cell).StartsWith(ClosedMark, StringComparison.Ordinal);

    /// <summary>Whether a Status cell reads disclosed: it opens with the disclosed mark.</summary>
    public static bool IsDisclosed(string cell) => Lead(cell).StartsWith(DisclosedMark, StringComparison.Ordinal);

    /// <summary>Whether a Status cell is exactly one of the registry's spellings.</summary>
    public static bool IsCanonical(string cell) => Cells.Contains(cell.Trim(), StringComparer.Ordinal);

    private static string Lead(string cell) => cell.TrimStart().TrimStart('*', '_', ' ');
}
