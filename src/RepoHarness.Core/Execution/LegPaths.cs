using System.Text.RegularExpressions;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// The directories one leg runs against, and the names configuration spells them by.
/// </summary>
/// <remarks>
/// The build directory is derived from the processor, the toolchain and the configuration, so no
/// tracked file can name it.
/// <para>
/// A configured command sometimes has to name a directory the configuration cannot spell. The build
/// directory is the case that forces it: <c>build/&lt;processor&gt;-&lt;toolchain&gt;-&lt;config&gt;</c>
/// differs per leg and is chosen by this tool, so a project that builds out of source cannot write
/// down where its own tests live. Measured: <c>ctest</c> started at the tree root of such a project
/// reports "No tests were found!!!" and exits in under a fifth of a second, having found 2204 tests
/// nowhere, while the same arguments with the build directory find all of them.
/// </para>
/// </remarks>
public sealed record LegPaths
{
    /// <summary>The directories one leg runs against.</summary>
    /// <param name="treeRoot">The leg's tree: its worktree, or the repository.</param>
    /// <param name="buildDirectory">
    /// The leg's variant-keyed build directory, or <see langword="null"/> where this run reaches no
    /// leg and therefore has none. Null is refused when something names it, never substituted.
    /// </param>
    public LegPaths(string treeRoot, string? buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        TreeRoot = treeRoot;
        BuildDirectory = string.IsNullOrWhiteSpace(buildDirectory) ? null : buildDirectory;
    }

    /// <summary>The leg's tree: its worktree, or the repository.</summary>
    public string TreeRoot { get; }

    /// <summary>
    /// The leg's variant-keyed build directory, which only the harness knows, or
    /// <see langword="null"/> where this run reaches no leg.
    /// </summary>
    public string? BuildDirectory { get; }

    /// <summary>The tree's <c>.harness-config</c> directory.</summary>
    public string HarnessDirectory => Path.Combine(TreeRoot, HarnessLayout.DirectoryName);
}

/// <summary>
/// What a caller means by a brace group this vocabulary does not own.
/// </summary>
/// <remarks>
/// The distinction is whose text the string is. A setting in <c>config.json</c> is written for this
/// tool, so a name it cannot fill in is a typo and refusing it names the line to fix. A step's run
/// line is a program's own text, where <c>${HOME}</c>, <c>${PWD}</c> and <c>awk '{print}'</c> are
/// ordinary and this tool owns none of them — refusing there would reject working action files for
/// using the shell.
/// </remarks>
public enum PlaceholderPolicy
{
    /// <summary>A name this vocabulary cannot fill in is a mistake. For settings this tool owns.</summary>
    Refuse,

    /// <summary>A brace group this vocabulary does not own is left exactly as written, for whoever does.</summary>
    LeaveAsWritten,
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
    /// <paramref name="value"/> with every placeholder this vocabulary owns replaced by the
    /// directory or value it names.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="paths">The leg's directories.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <param name="policy">What to do with a brace group this vocabulary does not own.</param>
    /// <param name="extra">
    /// Names this caller supplies beyond the directories, such as an action's declared inputs.
    /// Matched exactly: a lookup that ignored case would let an action declaring <c>home</c> replace
    /// a step's <c>${HOME}</c> with it.
    /// </param>
    /// <exception cref="HarnessException">A placeholder names nothing this caller can fill in.</exception>
    public static string Expand(
        string value,
        LegPaths paths,
        string setting,
        PlaceholderPolicy policy = PlaceholderPolicy.Refuse,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (value is null || !value.Contains('{', StringComparison.Ordinal))
        {
            return value ?? string.Empty;
        }

        return Placeholder.Replace(value, match =>
        {
            var name = match.Groups["name"].Value;

            if (name == BuildDirectory && paths.BuildDirectory is null)
            {
                // Refused, not filled in with the tree root. Substituting the tree root is exactly
                // the failure this vocabulary was written to end: ctest started at the tree root of
                // an out-of-source project reports no tests and exits in under a fifth of a second.
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{setting} names '{{{BuildDirectory}}}', and this run reaches no leg, so there is "
                    + "no build directory to put there. Run it for a leg, or take the name out.");
            }

            if (Path(name, paths) is { } directory)
            {
                return directory;
            }

            if (extra is not null && extra.TryGetValue(name, out var supplied))
            {
                return supplied;
            }

            Refuse(setting, name, policy, extra?.Keys);

            // Left exactly as written, braces and all, for whoever does own it.
            return match.Value;
        });
    }

    /// <summary>
    /// Refuses a configured string holding a placeholder nothing replaces, without needing the
    /// directories it would be expanded against.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <param name="policy">What to do with a brace group this vocabulary does not own.</param>
    /// <param name="extra">Names this caller will supply beyond the directories.</param>
    /// <exception cref="HarnessException">A placeholder names nothing this caller can fill in.</exception>
    /// <remarks>
    /// Called when the configuration is read, where no leg has been placed and no build directory
    /// exists yet. A typo found there names the line to fix; found when the leg runs it has already
    /// cost the build that preceded it.
    /// </remarks>
    public static void RefuseUnknown(
        string? value,
        string setting,
        PlaceholderPolicy policy = PlaceholderPolicy.Refuse,
        IReadOnlyCollection<string>? extra = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (Match match in Placeholder.Matches(value))
        {
            var name = match.Groups["name"].Value;

            if (Path(name, null) is not null || (extra is not null && extra.Contains(name)))
            {
                continue;
            }

            Refuse(setting, name, policy, extra);
        }
    }

    /// <summary>
    /// Throws for a name nothing can fill in, where this caller's policy says an unowned brace group
    /// is a mistake rather than somebody else's syntax.
    /// </summary>
    private static void Refuse(
        string setting,
        string name,
        PlaceholderPolicy policy,
        IEnumerable<string>? extra)
    {
        // Refused under either policy, because no other vocabulary spells a name that differs from
        // one of these only in case. This is the typo a reader cannot see: '{builddir}' reaches a
        // runner as literal text and ctest answers it by reporting no tests and exiting 8.
        var miscased = All.FirstOrDefault(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));

        if (miscased is not null)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{setting} names '{{{name}}}', which is '{{{miscased}}}' spelled differently. "
                + $"Write it as '{{{miscased}}}'.");
        }

        if (policy == PlaceholderPolicy.LeaveAsWritten)
        {
            return;
        }

        throw Unknown(setting, name, extra);
    }

    /// <summary>
    /// The directory <paramref name="name"/> spells, or null when nothing does. With no
    /// <paramref name="paths"/> it answers only whether the name is known.
    /// </summary>
    private static string? Path(string name, LegPaths? paths) => name switch
    {
        BuildDirectory => paths is null ? name : paths.BuildDirectory,
        TreeRoot => paths?.TreeRoot ?? name,
        HarnessDirectory => paths?.HarnessDirectory ?? name,
        _ => null,
    };

    private static HarnessException Unknown(string setting, string name, IEnumerable<string>? extra)
    {
        var known = All.Concat(extra ?? []).Select(spelled => $"{{{spelled}}}");

        return new HarnessException(
            HarnessExit.ConfigInvalid,
            $"{setting} names '{{{name}}}', which nothing here can fill in. "
            + $"The names are {string.Join(", ", known)}.");
    }
}
