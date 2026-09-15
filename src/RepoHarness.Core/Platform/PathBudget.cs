namespace RepoHarness.Core.Platform;

/// <summary>
/// Decides whether a directory leaves room for the paths a build will generate
/// beneath it.
/// </summary>
public interface IPathBudget
{
    /// <summary>Checks whether <paramref name="directory"/> can host a build tree.</summary>
    /// <param name="directory">Absolute path that would be created.</param>
    /// <param name="reserve">Longest path a build generates below the tree root.</param>
    /// <param name="margin">Extra headroom kept beyond the reserve.</param>
    /// <param name="limit">
    /// Path length to budget against instead of the platform's own limit, or
    /// <see langword="null"/> to use the platform's.
    /// </param>
    PathBudgetResult Check(string directory, int reserve, int margin, int? limit = null);
}

/// <inheritdoc cref="IPathBudget"/>
public sealed class PathBudget(IHostPlatform platform) : IPathBudget
{
    private readonly IHostPlatform _platform = platform;

    public PathBudgetResult Check(string directory, int reserve, int margin, int? limit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);
        ArgumentOutOfRangeException.ThrowIfNegative(margin);

        if ((limit ?? _platform.MaxPathLength) is not { } effectiveLimit)
        {
            return PathBudgetResult.Unbounded();
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(effectiveLimit, nameof(limit));

        var required = directory.Length + reserve + margin;
        var availableNameLength = effectiveLimit - reserve - margin - GetParentLength(directory);

        return required <= effectiveLimit
            ? PathBudgetResult.WithinBudget(required, effectiveLimit, availableNameLength)
            : PathBudgetResult.Exceeded(required, effectiveLimit, availableNameLength);
    }

    /// <summary>
    /// Length of everything up to and including the final separator, which is what a
    /// caller cannot change when it is only choosing the last path segment.
    /// </summary>
    private static int GetParentLength(string directory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(directory);
        var separator = trimmed.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator < 0 ? 0 : separator + 1;
    }
}

/// <summary>
/// Whether a path fits, and how much room is left for its final segment. Constructed
/// only through the factories, so an unbounded result never carries a limit and a
/// bounded one always does.
/// </summary>
public sealed record PathBudgetResult
{
    private PathBudgetResult(bool isWithinBudget, int requiredLength, int? limit, int? availableNameLength)
    {
        IsWithinBudget = isWithinBudget;
        RequiredLength = requiredLength;
        Limit = limit;
        AvailableNameLength = availableNameLength;
    }

    /// <summary>Whether the path fits.</summary>
    public bool IsWithinBudget { get; }

    /// <summary>
    /// Length the full path needs, including reserve and margin. Zero when no limit applies.
    /// </summary>
    public int RequiredLength { get; }

    /// <summary>The limit budgeted against, or <see langword="null"/> when none applies.</summary>
    public int? Limit { get; }

    /// <summary>
    /// Characters left for the final path segment, or <see langword="null"/> when no
    /// limit applies. Negative when even an empty name would not fit.
    /// </summary>
    public int? AvailableNameLength { get; }

    /// <summary>No limit applies, as on Linux and macOS.</summary>
    public static PathBudgetResult Unbounded() => new(true, 0, null, null);

    /// <summary>The path fits within <paramref name="limit"/>.</summary>
    public static PathBudgetResult WithinBudget(int requiredLength, int limit, int availableNameLength)
        => new(true, requiredLength, limit, availableNameLength);

    /// <summary>The path does not fit within <paramref name="limit"/>.</summary>
    public static PathBudgetResult Exceeded(int requiredLength, int limit, int availableNameLength)
        => new(false, requiredLength, limit, availableNameLength);

    /// <summary>Explains the result in terms the caller can act on.</summary>
    public string Describe(string directory)
    {
        if (Limit is not { } limit)
        {
            return $"'{directory}' is not subject to a path length limit here.";
        }

        if (IsWithinBudget)
        {
            return $"'{directory}' needs {RequiredLength} of the {limit} characters allowed.";
        }

        return $"'{directory}' needs {RequiredLength} characters but the limit is {limit}. "
            + (AvailableNameLength > 0
                ? $"A name of at most {AvailableNameLength} characters would fit here."
                : "No name fits here; move the repository to a shorter path.");
    }
}
