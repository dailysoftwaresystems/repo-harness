namespace RepoHarness.Core.Configuration;

/// <summary>Where the anchor registries live, and how a new anchor id is spelled.</summary>
public sealed class AnchorSettings
{
    /// <summary>The pending registry used when config.json names none.</summary>
    public const string DefaultPendingAnchorsPath = ".plans/_deferred-anchor-registry.md";

    /// <summary>The done registry used when config.json names none.</summary>
    public const string DefaultDoneAnchorsPath = ".plans/_deferred-anchor-registry-done.md";

    /// <summary>
    /// The registry of live anchors (open, gated and disclosed), relative to the repository root.
    /// </summary>
    public string PendingAnchorsPath { get; init; } = DefaultPendingAnchorsPath;

    /// <summary>
    /// The archive of closed anchors, relative to the repository root. An anchor moves here when
    /// its status becomes closed, and back when it is reopened.
    /// </summary>
    public string DoneAnchorsPath { get; init; } = DefaultDoneAnchorsPath;

    /// <summary>What every anchor id starts with, before its first hyphen.</summary>
    public string IdPrefix { get; init; } = "D";

    /// <summary>
    /// Fewest hyphen-separated segments after the prefix that a NEW id may have. Existing ids are
    /// never checked against it: an id is permanent once written, because renaming one orphans
    /// every citation of it.
    /// </summary>
    public int MinimumIdSegments { get; init; } = 3;
}
