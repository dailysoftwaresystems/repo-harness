using System.Text.Json.Serialization;

namespace RepoHarness.Core.Configuration;

/// <summary>Commit message templating and policy.</summary>
public sealed class CommitConfig
{
    /// <summary>
    /// Whether to add a Developer Certificate of Origin sign-off. On by default,
    /// because a project that requires it rejects unsigned commits only after they
    /// are already pushed, when rewriting them is most expensive.
    /// </summary>
    public bool SignOff { get; init; } = true;

    /// <summary>Message template; <c>{name}</c> placeholders are filled from variables.</summary>
    public string? Template { get; init; }

    /// <summary>Declared template variables, keyed by placeholder name.</summary>
    public Dictionary<string, CommitVariable> Variables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One commit template variable.</summary>
public sealed class CommitVariable
{
    /// <summary>Whether a value must be supplied.</summary>
    public bool Required { get; init; }

    /// <summary>Value used when none is supplied.</summary>
    public string? Default { get; init; }

    /// <summary>What the variable means, shown when a required one is missing.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Processes outside the harness that can corrupt a leg's result while it runs. Sampled by every
/// leg-running command, machine-wide rather than over its own process tree: a lock cannot catch a
/// tool somebody started by hand, because a tool started by hand never takes it.
/// </summary>
public sealed class ContentionConfig
{
    /// <summary>
    /// Build and test tools that, found running against a leg's build directory from outside
    /// the harness's own process tree, make the leg's result untrustworthy: its verdict is
    /// <c>contended</c> rather than passed or failed. A lock cannot catch these, because a
    /// tool someone started by hand never takes the harness's lock.
    /// </summary>
    public List<string> BuildTools { get; init; } =
        ["ctest", "ninja", "cmake", "make", "gmake", "msbuild", "dotnet", "dart", "flutter"];

    /// <summary>
    /// Tools that share state outside any build directory, such as a per-user cache, reported
    /// as a warning when one runs outside the harness during a leg. Never a refusal: several
    /// legs legitimately run them at the same time.
    /// </summary>
    public List<string> SharedResourceTools { get; init; } = [];

    /// <summary>
    /// What each of <see cref="SharedResourceTools"/> shares, by tool name, said in the warning when
    /// one is found beside a leg - "the per-user compiler cache under ~/.cache/dsscp".
    /// </summary>
    /// <remarks>
    /// The harness cannot know what a tool shares; the file's author does. Without it the warning can
    /// only say that the tool shares something, and a reader cannot judge whether that matters.
    /// </remarks>
    public Dictionary<string, string> SharedState { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What the tree mirror carries, what it must never carry, and how far a deletion may go.</summary>
public sealed class SyncConfig
{
    /// <summary>The share of a host's copy a single sync may delete when none is configured.</summary>
    public const double DefaultMaxDeleteFraction = 0.25;

    /// <summary>
    /// Paths sync must never transfer, whatever the configuration says: the repository's own
    /// metadata.
    /// </summary>
    /// <remarks>
    /// A constant rather than a default value. A default is replaced by whatever list a
    /// configuration supplies, so a floor held that way can be removed by the very
    /// configuration it exists to constrain.
    /// <para>
    /// The harness's own directory is no longer listed here, and is no less protected for it. Its
    /// state - connection data, credentials, runner values, locks, runs - is withheld by
    /// <see cref="Sync.HarnessDirectorySync"/>, in code that no configuration and no ignore rule
    /// reaches. What left this list is the one part of that directory that belongs to the tree:
    /// its actions, which a leg on another host has to be able to run.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> NeverTransferFloor { get; } = [".git"];

    /// <summary>Paths to exclude in addition to those git already ignores.</summary>
    public List<string> Exclude { get; init; } = [];

    /// <summary>
    /// Further paths that must never be transferred. These are added to
    /// <see cref="NeverTransferFloor"/>: declaring this list cannot remove anything from
    /// the floor. Build output belongs here by default because it is large and
    /// meaningless on another machine.
    /// </summary>
    public List<string> NeverTransfer { get; init; } = ["build"];

    /// <summary>
    /// The share of the files already in a host's copy that one sync may delete before it stops and
    /// reports instead. Zero allows any deletion.
    /// </summary>
    /// <remarks>
    /// Deletions must propagate, or a copy that only ever gains files stops being a copy: a deleted
    /// source file keeps compiling and a renamed one exists twice, and the leg's verdict then
    /// describes a tree that no longer exists. The bound is what keeps that from emptying a host when
    /// a repository path is mistyped and the source appears, correctly, to contain nothing.
    /// </remarks>
    public double MaxDeleteFraction { get; init; } = DefaultMaxDeleteFraction;

    /// <summary>
    /// The floor together with <see cref="NeverTransfer"/>: everything sync must withhold, and
    /// equally everything it must never delete. A path the harness will not write is one it cannot
    /// know the source lacks, so deleting it would remove the host's own state — its git repository,
    /// its harness configuration, and the build directories that make an incremental build possible.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveNeverTransfer
        => [.. NeverTransferFloor.Concat(NeverTransfer).Distinct(StringComparer.Ordinal)];
}
