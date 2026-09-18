using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Build;

/// <summary>What sampling watches for during one leg, from the configuration every verb reads.</summary>
/// <remarks>
/// One construction for build, test and run. Built at each verb, it was three copies of the same
/// four settings, and a fourth setting added to one of them would have been a verb that watched
/// for less than the others.
/// </remarks>
public static class ContentionRequests
{
    /// <summary>The request for <paramref name="leg"/>, building in <paramref name="buildDirectory"/>.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg being watched.</param>
    /// <param name="buildDirectory">The leg's own build directory.</param>
    /// <param name="treeRoot">The tree the leg builds, which every other leg on this machine builds in too.</param>
    /// <param name="platformKey">This machine's platform, which decides each other leg's variant.</param>
    public static ContentionRequest For(
        HarnessConfig config,
        string leg,
        string buildDirectory,
        string treeRoot,
        string platformKey)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        return new ContentionRequest
        {
            Leg = leg,
            BuildDirectory = buildDirectory,
            BuildTools = config.Contention.BuildTools,
            SharedResourceTools = config.Contention.SharedResourceTools,
            SampleSeconds = config.Defaults.ProcessSampleSeconds,
            OtherLegs = OtherLegs(config, leg, treeRoot, platformKey),
        };
    }

    /// <summary>
    /// Every other declared leg this machine could build, by name, with the build directory it would
    /// build in under <paramref name="treeRoot"/>.
    /// </summary>
    /// <remarks>
    /// What lets a process be said to belong to a sibling. Each leg a host runs is run by a harness
    /// process of its own, so a sibling's compilers are outside this leg's process tree and read, to
    /// this leg, exactly as a stranger's would. Legs placed on one host build in one copy, so every
    /// sibling's directory is under the same root as this leg's. Only legs of this machine's
    /// operating system are listed: no other kind ever builds here.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> OtherLegs(
        HarnessConfig config,
        string leg,
        string treeRoot,
        string platformKey)
        => config.Legs
            .Where(pair => !string.Equals(pair.Key, leg, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.Value.Os, platformKey, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key,
                pair => VariantKey.For(config, pair.Value, platformKey).DirectoryUnder(treeRoot),
                StringComparer.OrdinalIgnoreCase);
}
