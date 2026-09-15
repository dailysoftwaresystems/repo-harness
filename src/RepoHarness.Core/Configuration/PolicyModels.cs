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
/// Processes outside the harness that can corrupt a leg's result while it runs. Consumed by
/// the leg runner, which is not implemented yet.
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
}

/// <summary>
/// What the tree mirror carries, and what it must never carry. Consumed by the sync
/// command, which is not implemented yet: the section is validated now so that a
/// configuration written today keeps working when that command arrives.
/// </summary>
public sealed class SyncConfig
{
    /// <summary>
    /// Paths sync must never transfer, whatever the configuration says: repository
    /// metadata, and the harness's own state including its ssh secrets.
    /// </summary>
    /// <remarks>
    /// A constant rather than a default value. A default is replaced by whatever list a
    /// configuration supplies, so a floor held that way can be removed by the very
    /// configuration it exists to constrain.
    /// </remarks>
    public static IReadOnlyList<string> NeverTransferFloor { get; } = [".git", ".harness-config"];

    /// <summary>Paths to exclude in addition to those git already ignores.</summary>
    public List<string> Exclude { get; init; } = [];

    /// <summary>
    /// Further paths that must never be transferred. These are added to
    /// <see cref="NeverTransferFloor"/>: declaring this list cannot remove anything from
    /// the floor. Build output belongs here by default because it is large and
    /// meaningless on another machine.
    /// </summary>
    public List<string> NeverTransfer { get; init; } = ["build"];

    /// <summary>The floor together with <see cref="NeverTransfer"/>: everything sync must withhold.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveNeverTransfer
        => [.. NeverTransferFloor.Concat(NeverTransfer).Distinct(StringComparer.Ordinal)];
}
