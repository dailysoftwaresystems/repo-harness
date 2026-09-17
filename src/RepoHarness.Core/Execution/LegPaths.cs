using System.Text.RegularExpressions;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// The directories one leg runs against, and the names configuration spells them by.
/// </summary>
/// <param name="TreeRoot">The leg's tree: its worktree, or the repository.</param>
/// <param name="BuildDirectory">
/// The leg's variant-keyed build directory, which only the harness knows: it is derived from the
/// processor, the toolchain and the configuration, so no tracked file can name it.
/// </param>
/// <remarks>
/// A configured command sometimes has to name a directory the configuration cannot spell. The build
/// directory is the case that forces it: <c>build/&lt;processor&gt;-&lt;toolchain&gt;-&lt;config&gt;</c>
/// differs per leg and is chosen by this tool, so a project that builds out of source cannot write
/// down where its own tests live. Measured: <c>ctest</c> started at the tree root of such a project
/// reports "No tests were found!!!" and exits in under a fifth of a second, having found 2204 tests
/// nowhere, while the same arguments with the build directory find all of them.
/// </remarks>
public sealed record LegPaths(string TreeRoot, string BuildDirectory)
{
    /// <summary>The tree's <c>.harness-config</c> directory.</summary>
    public string HarnessDirectory => Path.Combine(TreeRoot, HarnessLayout.DirectoryName);
}

/// <summary>
/// The directories a configured command may name, and how a name is replaced with a path.
/// </summary>
/// <remarks>
/// One vocabulary, expanded wherever a configured string may hold one, rather than a key per
/// question. <c>coresArgs</c> already established the shape with <c>{cores}</c>; this is the same
/// idea for the directories a leg runs against, and the same refusal when a name is not one of
/// them.
/// <para>
/// A name nothing replaces is refused when <c>config.json</c> is read, never passed through. Passed
/// through it reaches the runner as the literal text <c>{buildDir}</c>, and what a runner does with
/// a directory that cannot exist is its own business: ctest reports no tests and exits 8, which
/// reads as a suite that ran and found nothing.
/// </para>
/// </remarks>
public static partial class LegPathNames
{
    /// <summary>Names the leg's variant-keyed build directory.</summary>
    public const string BuildDirectory = "buildDir";

    /// <summary>Names the leg's tree root.</summary>
    public const string TreeRoot = "treeDir";

    /// <summary>Names the tree's <c>.harness-config</c> directory.</summary>
    public const string HarnessDirectory = "harnessDir";

    /// <summary>Every name, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [BuildDirectory, TreeRoot, HarnessDirectory];

    /// <summary>
    /// A placeholder as it is written: a name in braces. Matched rather than searched for by name so
    /// that one nobody declared is found and refused, instead of surviving into a command line.
    /// </summary>
    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder { get; }

    /// <summary>
    /// <paramref name="value"/> with every placeholder replaced by the directory it names.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="paths">The leg's directories.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <exception cref="HarnessException">A placeholder names nothing.</exception>
    public static string Expand(string value, LegPaths paths, string setting)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (value is null || !value.Contains('{', StringComparison.Ordinal))
        {
            return value ?? string.Empty;
        }

        return Placeholder.Replace(value, match =>
        {
            var name = match.Groups["name"].Value;

            return Path(name, paths) ?? throw Unknown(setting, name);
        });
    }

    /// <summary>
    /// Refuses a configured string holding a placeholder nothing replaces, without needing the
    /// directories it would be expanded against.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <exception cref="HarnessException">A placeholder names nothing.</exception>
    /// <remarks>
    /// Called when the configuration is read, where no leg has been placed and no build directory
    /// exists yet. A typo found there names the line to fix; found when the leg runs it has already
    /// cost the build that preceded it.
    /// </remarks>
    public static void RefuseUnknown(string? value, string setting)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (Match match in Placeholder.Matches(value))
        {
            var name = match.Groups["name"].Value;

            // {cores} is expanded elsewhere, by the runner's own core-count rule, and a string
            // holding one is not this vocabulary's to refuse.
            if (!string.Equals(name, CoresName, StringComparison.Ordinal) && Path(name, null) is null)
            {
                throw Unknown(setting, name);
            }
        }
    }

    /// <summary>The placeholder the core-count rule owns, which this vocabulary leaves alone.</summary>
    private const string CoresName = "cores";

    /// <summary>
    /// The directory <paramref name="name"/> spells, or null when nothing does. With no
    /// <paramref name="paths"/> it answers only whether the name is known.
    /// </summary>
    private static string? Path(string name, LegPaths? paths) => name switch
    {
        BuildDirectory => paths?.BuildDirectory ?? name,
        TreeRoot => paths?.TreeRoot ?? name,
        HarnessDirectory => paths?.HarnessDirectory ?? name,
        _ => null,
    };

    private static HarnessException Unknown(string setting, string name)
        => new(
            HarnessExit.ConfigInvalid,
            $"{setting} names '{{{name}}}', which is not a directory this tool can fill in. "
            + $"The names are {string.Join(", ", All.Select(known => $"{{{known}}}"))}.");
}
