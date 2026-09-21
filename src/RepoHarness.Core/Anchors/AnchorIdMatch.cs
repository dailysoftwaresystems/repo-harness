namespace RepoHarness.Core.Anchors;

/// <summary>How an anchor id - asked for by read-anchor, or cited in a scanned root - finds its row.</summary>
/// <remarks>
/// Exactly, and case counts: an id is a name, and a near miss is a different anchor. One rule for
/// every verb. Resolved by containment instead, a citation of <c>D-FF3-3</c> passed through a row
/// <c>D-FF3-30-...</c>, a wrapped fragment <c>D-PP-PRESCAN-</c> passed through the id it was cut
/// from, and check-anchor-citations called a row present that read-anchor, asked for the same id,
/// did not find - so a truncated or ambiguous citation was invisible to the gate.
/// </remarks>
public static class AnchorIdMatch
{
    /// <summary>How two anchor ids compare.</summary>
    public static StringComparer Comparer { get; } = StringComparer.Ordinal;
}
