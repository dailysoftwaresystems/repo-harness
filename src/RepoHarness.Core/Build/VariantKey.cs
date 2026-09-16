using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Build;

/// <summary>
/// What makes one build directory different from another, and the directory name it produces.
/// </summary>
/// <param name="Processor">The processor the build targets.</param>
/// <param name="Toolchain">The toolchain that builds it.</param>
/// <param name="Config">The build configuration.</param>
/// <param name="Sanitizer">The instrumentation overlay, if any.</param>
/// <remarks>
/// Every one of these four changes what the compiler is asked to produce, and a build system refuses
/// to change some of them on an existing cache while silently accepting others. Keying the directory
/// by all four is what stops two legs from writing objects into one place: MSVC and GCC cannot share
/// <c>build/release</c>, and neither can a native build and a cross-build.
/// </remarks>
public sealed record VariantKey(string Processor, string Toolchain, string Config, string? Sanitizer)
{
    /// <summary>
    /// The instrumentation overlay, with "none declared" spelt one way.
    /// </summary>
    /// <remarks>
    /// An empty string and no string are the same absence, and <c>"sanitizer": ""</c> is reachable
    /// from a hand-edited configuration. Left as two values they are one build directory and two
    /// keys, which surfaces as a shared-build-directory refusal naming two legs their author
    /// believes are different.
    /// </remarks>
    public string? Sanitizer { get; init; } = string.IsNullOrEmpty(Sanitizer) ? null : Sanitizer;

    /// <summary>The directory name this variant builds in, below the tree's build root.</summary>
    public string DirectoryName => Sanitizer is null
        ? $"{Processor}-{Toolchain}-{Config}"
        : $"{Processor}-{Toolchain}-{Config}-{Sanitizer}";

    /// <summary>The build directory itself, always derived from the leg's own tree root.</summary>
    /// <param name="treeRoot">The root of the tree this leg acts on.</param>
    /// <remarks>
    /// Tree-rooted, never resolved against the current directory or the main checkout: a worktree's
    /// build output landing in the main checkout's build directory is how two legs quietly read each
    /// other's objects.
    /// </remarks>
    public string DirectoryUnder(string treeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        return Path.Combine(treeRoot, BuildRootName, DirectoryName);
    }

    /// <summary>The directory every variant's build directory sits under.</summary>
    public const string BuildRootName = "build";

    /// <summary>
    /// The toolchain part of a variant whose leg compiles nothing.
    /// </summary>
    /// <remarks>
    /// A leg that only runs a predefined runner needs no project and no compiler, but it still needs
    /// a variant: the lock is keyed by one, and so is the directory contention watches. Naming the
    /// absence keeps those keys distinct from a leg that does build, rather than making a repository
    /// declare a project it has no use for.
    /// </remarks>
    public const string NoToolchain = "none";

    /// <summary>Reads the variant a leg describes, filling in what it leaves to the project.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="platformKey">The operating system the leg runs on, which picks a default toolchain.</param>
    public static VariantKey For(HarnessConfig config, LegConfig leg, string platformKey)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);

        var project = ProjectFor(config, leg);

        var toolchain = leg.Toolchain
            ?? (project is not null && project.DefaultToolchain.TryGetValue(platformKey, out var byPlatform)
                ? byPlatform
                : project?.DefaultToolchain.GetValueOrDefault("all"))
            ?? NoToolchain;

        return new VariantKey(leg.Processor, toolchain, leg.Config, leg.Sanitizer);
    }

    /// <summary>Whether this variant names a compiler, and so can be built.</summary>
    public bool Buildable => !string.Equals(Toolchain, NoToolchain, StringComparison.Ordinal);

    /// <summary>The project a leg builds, which is its own or the configured default.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    public static ProjectConfig? ProjectFor(HarnessConfig config, LegConfig leg)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);

        var name = leg.Project ?? config.Defaults.Project;

        return name is null
            ? config.Projects.Count == 1 ? config.Projects[0] : null
            : config.Projects.FirstOrDefault(
                project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
