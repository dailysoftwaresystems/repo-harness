namespace RepoHarness.Core.Platform;

/// <summary>
/// How a configured value is narrowed to one platform, in the two shapes the file uses.
/// </summary>
/// <remarks>
/// <para>
/// A setting is scoped to a platform in one of two ways, and the difference is what question is
/// being asked. A <em>list</em> answers "does this entry apply here" — a tool needed only on
/// Windows, a toolchain that exists only on Linux — and <see cref="Applies"/> reads it. A
/// <em>map</em> answers "what is this entry's value here" — where an installer lives, what a built
/// program is called — and <see cref="Select{TValue}"/> reads it.
/// </para>
/// <para>
/// Both spell every platform <c>all</c>, both compare ignoring case, and both live here rather than
/// beside each caller because the rule was previously written out at each one. Four copies of
/// "try the platform, then try 'all'" agreed by luck; a fifth written for a new setting is how they
/// stop agreeing.
/// </para>
/// </remarks>
public static class PlatformScope
{
    /// <summary>The key that names every platform, in a list and in a map alike.</summary>
    public const string Every = "all";

    /// <summary>
    /// Whether an entry scoped to <paramref name="platforms"/> applies on
    /// <paramref name="platformKey"/>.
    /// </summary>
    /// <remarks>
    /// An empty list applies everywhere, as <see cref="Every"/> does: a setting that named no
    /// platform at all would otherwise apply nowhere, which is never what leaving a list out means.
    /// An unmeasured platform also applies, because refusing what cannot be placed would turn a
    /// host nobody could measure into a host that needs nothing.
    /// </remarks>
    /// <param name="platforms">The platforms the entry names, or <see cref="Every"/>.</param>
    /// <param name="platformKey">The platform being asked about, as <see cref="PlatformNames"/> spells it.</param>
    public static bool Applies(IReadOnlyList<string>? platforms, string? platformKey)
    {
        if (platforms is null || platforms.Count == 0 || string.IsNullOrWhiteSpace(platformKey))
        {
            return true;
        }

        return platforms.Any(platform
            => string.Equals(platform, Every, StringComparison.OrdinalIgnoreCase)
            || string.Equals(platform, platformKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The value <paramref name="byPlatform"/> declares for <paramref name="platformKey"/>, falling
    /// back to the one declared for <see cref="Every"/>, or <see langword="default"/> when neither
    /// is declared.
    /// </summary>
    /// <remarks>
    /// The platform's own entry wins over <see cref="Every"/>, so a map can state the general case
    /// once and override it where the general case is wrong. That order is the whole point of the
    /// shape: reversed, the override would never be reached.
    /// </remarks>
    /// <typeparam name="TValue">What the map holds.</typeparam>
    /// <param name="byPlatform">The map, keyed by platform name or <see cref="Every"/>.</param>
    /// <param name="platformKey">The platform being asked about.</param>
    public static TValue? Select<TValue>(IReadOnlyDictionary<string, TValue>? byPlatform, string? platformKey)
    {
        if (byPlatform is null || byPlatform.Count == 0)
        {
            return default;
        }

        if (!string.IsNullOrWhiteSpace(platformKey) && TryGet(byPlatform, platformKey, out var forPlatform))
        {
            return forPlatform;
        }

        return TryGet(byPlatform, Every, out var forEvery) ? forEvery : default;
    }

    /// <summary>
    /// Whether <paramref name="byPlatform"/> declares anything at all for
    /// <paramref name="platformKey"/>, counting <see cref="Every"/>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Select{TValue}"/> returning <see langword="null"/>, because a map
    /// may legitimately hold a null value; a caller refusing an entry that covers no platform needs
    /// to know which of the two it has.
    /// </remarks>
    /// <typeparam name="TValue">What the map holds.</typeparam>
    /// <param name="byPlatform">The map, keyed by platform name or <see cref="Every"/>.</param>
    /// <param name="platformKey">The platform being asked about.</param>
    public static bool Covers<TValue>(IReadOnlyDictionary<string, TValue>? byPlatform, string? platformKey)
    {
        if (byPlatform is null || byPlatform.Count == 0)
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(platformKey) && TryGet(byPlatform, platformKey, out _))
            || TryGet(byPlatform, Every, out _);
    }

    /// <summary>
    /// Reads one key, ignoring case whatever comparer the map was built with.
    /// </summary>
    /// <remarks>
    /// A configuration's maps are built case-insensitively, but a map assembled in code need not be,
    /// and a lookup that silently missed would read as a platform declaring nothing.
    /// </remarks>
    private static bool TryGet<TValue>(
        IReadOnlyDictionary<string, TValue> byPlatform,
        string key,
        out TValue? value)
    {
        if (byPlatform.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var (declared, declaredValue) in byPlatform)
        {
            if (string.Equals(declared, key, StringComparison.OrdinalIgnoreCase))
            {
                value = declaredValue;
                return true;
            }
        }

        value = default;
        return false;
    }
}
