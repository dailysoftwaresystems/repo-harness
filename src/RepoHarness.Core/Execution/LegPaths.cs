using System.Text.RegularExpressions;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// Which leg a piece of work is, in the words a configured command can spell.
/// </summary>
/// <remarks>
/// Derived, never supplied. An instrument that records which host it measured has to get that name
/// from somewhere, and a value typed into a run line by hand can be typed wrongly: a benchmark
/// labelled with the wrong processor is worse than one labelled with nothing, because the number
/// looks comparable and is not. The harness already knows every one of these at the moment a step
/// starts; until now it had no way to say them.
/// </remarks>
/// <param name="Leg">The leg's name, as <c>legs</c> declares it.</param>
/// <param name="Os">The operating system the work ran on.</param>
/// <param name="Processor">The processor the leg targets, which under emulation is not the host's.</param>
/// <param name="Toolchain">The toolchain, or <c>none</c> for a leg that builds nothing.</param>
/// <param name="Config">The build configuration.</param>
/// <param name="Variant">The variant directory's name: processor, toolchain, config and any sanitizer.</param>
/// <param name="Host">The host the work ran on, as this tool names it.</param>
/// <param name="RunId">The run this work belongs to.</param>
public sealed record LegIdentity(
    string Leg,
    string Os,
    string Processor,
    string Toolchain,
    string Config,
    string Variant,
    string Host,
    string RunId);

/// <summary>
/// The directories one leg runs against, who that leg is, and what its build produces.
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

    /// <summary>
    /// Which leg this is, or <see langword="null"/> for a caller that has no leg in hand.
    /// </summary>
    public LegIdentity? Identity { get; init; }

    /// <summary>
    /// The one file this leg's build is declared to produce, or <see langword="null"/> when the
    /// project declares none for this platform, or declares several.
    /// </summary>
    /// <remarks>
    /// One, or nothing. <c>buildOutputs</c> is a list and every entry in it must exist for the build
    /// to be witnessed, so "the product" is only a well-formed question when the list holds exactly
    /// one answer for this platform. Where it holds several, naming it is refused rather than
    /// guessed: an instrument pointed at the wrong one of three binaries reports a measurement of
    /// something nobody asked about, and reports it as a success.
    /// </remarks>
    public string? Product { get; init; }

    /// <summary>Why <see cref="Product"/> is null, for a refusal that can say which case it is.</summary>
    public string? ProductProblem { get; init; }

    /// <summary>
    /// Where this run of the action writes while it runs, or <see langword="null"/> outside an
    /// action.
    /// </summary>
    public string? ActionBuild { get; init; }

    /// <summary>
    /// Where this run of the action's persisted outputs are kept, or <see langword="null"/> outside
    /// an action.
    /// </summary>
    public string? ActionArtifacts { get; init; }

    /// <summary>
    /// This step's own directory under <see cref="ActionBuild"/>, or <see langword="null"/> outside
    /// a step.
    /// </summary>
    public string? StepBuild { get; init; }
}

/// <summary>
/// What a caller means by a brace group this vocabulary does not own.
/// </summary>
/// <remarks>
/// The distinction is whose text the string is. A setting in <c>config.json</c> and a step's run
/// line are both written for this tool, so a name it cannot fill in is a typo and refusing it names
/// the line to fix. What a brace group is never allowed to be is passed through silently: a run line
/// holding <c>{greeting}</c> reached the program as those nine characters and the leg reported
/// passed, which is a wrapper reporting success without doing what it was asked.
/// <para>
/// <c>${NAME}</c> is another expander's syntax and is always left alone, and a literal brace is
/// written doubled: <c>{{</c> and <c>}}</c>. That is what keeps <c>awk '{{print}}'</c> expressible
/// while <c>{print}</c> is still refused rather than guessed at.
/// </para>
/// </remarks>
public enum PlaceholderPolicy
{
    /// <summary>A name this vocabulary cannot fill in is a mistake.</summary>
    Refuse,

    /// <summary>A brace group this vocabulary does not own is left exactly as written.</summary>
    LeaveAsWritten,
}

/// <summary>
/// The names a configured command may spell, and how a name is replaced with its value.
/// </summary>
/// <remarks>
/// One vocabulary, expanded wherever a configured string may hold one, rather than a key per
/// question. <c>coresArgs</c> established the shape with <c>{cores}</c>; this is the same idea for
/// the directories a leg runs against, who that leg is, and what its build produces.
/// <para>
/// A name nothing replaces is refused, never passed through. Passed through it reaches the runner as
/// the literal text <c>{buildDir}</c>, and what a runner does with a directory that cannot exist is
/// its own business: ctest reports no tests and exits 8, which reads as a suite that ran and found
/// nothing.
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

    /// <summary>Names the leg, as <c>legs</c> declares it.</summary>
    public const string Leg = "leg";

    /// <summary>Names the operating system the work ran on.</summary>
    public const string Os = "os";

    /// <summary>Names the processor the leg targets.</summary>
    public const string Processor = "processor";

    /// <summary>Names the toolchain.</summary>
    public const string Toolchain = "toolchain";

    /// <summary>Names the build configuration.</summary>
    public const string Config = "config";

    /// <summary>Names the variant directory: processor, toolchain, config and any sanitizer.</summary>
    public const string Variant = "variant";

    /// <summary>Names the host the work ran on.</summary>
    public const string Host = "host";

    /// <summary>Names the run this work belongs to.</summary>
    public const string RunId = "runId";

    /// <summary>Names the one file this leg's build is declared to produce.</summary>
    public const string Product = "product";

    /// <summary>Names where this run of the action writes while it runs.</summary>
    public const string ActionBuild = "actionBuild";

    /// <summary>Names where this run of the action's persisted outputs are kept.</summary>
    public const string ActionArtifacts = "actionArtifacts";

    /// <summary>Names this step's own directory under the action's build directory.</summary>
    public const string StepBuild = "stepBuild";

    /// <summary>Every name, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        BuildDirectory, TreeRoot, HarnessDirectory,
        Leg, Os, Processor, Toolchain, Config, Variant, Host, RunId,
        Product, ActionBuild, ActionArtifacts, StepBuild,
    ];

    /// <summary>
    /// A brace group as it is written. Three shapes, in the order they are recognised: a doubled
    /// brace, which is how a literal one is written; another expander's <c>${NAME}</c>, which this
    /// one never touches; and a name in braces, which is this vocabulary's.
    /// </summary>
    /// <remarks>
    /// Matched rather than searched for by name, so that a name nobody declared is found and refused
    /// instead of surviving into a command line.
    /// </remarks>
    [GeneratedRegex(
        @"(?<doubled>\{\{|\}\})|(?<other>\$\{[^}]*\})|\{(?<name>[A-Za-z][A-Za-z0-9]*)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder { get; }

    /// <summary>
    /// <paramref name="value"/> with every placeholder this vocabulary owns replaced by what it
    /// names, every doubled brace reduced to one, and everything else left as written.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="paths">The leg's directories, identity and product.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <param name="policy">What to do with a brace group this vocabulary does not own.</param>
    /// <param name="extra">
    /// Names this caller supplies beyond the built-in ones, such as an action's declared inputs.
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

        if (value is null || (!value.Contains('{', StringComparison.Ordinal)
            && !value.Contains('}', StringComparison.Ordinal)))
        {
            return value ?? string.Empty;
        }

        return Placeholder.Replace(value, match =>
        {
            if (match.Groups["doubled"].Success)
            {
                // '{{' is how a run line writes a brace it means literally, so awk '{{print}}'
                // reaches awk as '{print}' and this vocabulary never guesses at it.
                return match.Value[..1];
            }

            if (match.Groups["other"].Success)
            {
                return match.Value;
            }

            var name = match.Groups["name"].Value;

            if (Value(name, paths) is { } resolved)
            {
                return resolved;
            }

            if (extra is not null && extra.TryGetValue(name, out var supplied))
            {
                return supplied;
            }

            RefuseKnownButAbsent(setting, name, paths);
            Refuse(setting, name, policy, extra?.Keys);

            // Left exactly as written, braces and all, for whoever does own it.
            return match.Value;
        });
    }

    /// <summary>
    /// Refuses a configured string holding a placeholder nothing replaces, without needing the
    /// values it would be expanded against.
    /// </summary>
    /// <param name="value">The configured string.</param>
    /// <param name="setting">What to call the setting in a refusal.</param>
    /// <param name="policy">What to do with a brace group this vocabulary does not own.</param>
    /// <param name="extra">Names this caller will supply beyond the built-in ones.</param>
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
            if (match.Groups["doubled"].Success || match.Groups["other"].Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value;

            if (Known(name) || (extra is not null && extra.Contains(name)))
            {
                continue;
            }

            Refuse(setting, name, policy, extra);
        }
    }

    /// <summary>
    /// Throws where the name is one of this vocabulary's but this leg has nothing to put there, so
    /// the message says which case it is instead of claiming nothing can fill it in.
    /// </summary>
    private static void RefuseKnownButAbsent(string setting, string name, LegPaths paths)
    {
        switch (name)
        {
            case BuildDirectory when paths.BuildDirectory is null:
                // Refused, not filled in with the tree root. Substituting the tree root is exactly
                // the failure this vocabulary was written to end: ctest started at the tree root of
                // an out-of-source project reports no tests and exits in under a fifth of a second.
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{setting} names '{{{BuildDirectory}}}', and this run reaches no leg, so there is "
                    + "no build directory to put there. Run it for a leg, or take the name out.");

            case Product:
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"{setting} names '{{{Product}}}', and {paths.ProductProblem ?? "this leg has no build product"}.");

            case ActionBuild or ActionArtifacts or StepBuild when paths.ActionBuild is null:
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"{setting} names '{{{name}}}', and this runner declares phases rather than an "
                    + "action, so there is no action directory to put there. Only a step of an "
                    + "action file has one.");

            case Leg or Os or Processor or Toolchain or Config or Variant or Host or RunId
                when paths.Identity is null:
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{setting} names '{{{name}}}', and this run reaches no leg, so there is nothing "
                    + "to put there. Run it for a leg, or take the name out.");

            default:
                break;
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

    /// <summary>Whether <paramref name="name"/> is one this vocabulary owns.</summary>
    private static bool Known(string name) => All.Contains(name, StringComparer.Ordinal);

    /// <summary>What <paramref name="name"/> spells for this leg, or null when nothing does.</summary>
    private static string? Value(string name, LegPaths paths) => name switch
    {
        BuildDirectory => paths.BuildDirectory,
        TreeRoot => paths.TreeRoot,
        HarnessDirectory => paths.HarnessDirectory,
        Product => paths.Product,
        ActionBuild => paths.ActionBuild,
        ActionArtifacts => paths.ActionArtifacts,
        StepBuild => paths.StepBuild,
        Leg => paths.Identity?.Leg,
        Os => paths.Identity?.Os,
        Processor => paths.Identity?.Processor,
        Toolchain => paths.Identity?.Toolchain,
        Config => paths.Identity?.Config,
        Variant => paths.Identity?.Variant,
        Host => paths.Identity?.Host,
        RunId => paths.Identity?.RunId,
        _ => null,
    };

    private static HarnessException Unknown(string setting, string name, IEnumerable<string>? extra)
    {
        var known = All.Concat(extra ?? []).Select(spelled => $"{{{spelled}}}");

        return new HarnessException(
            HarnessExit.ConfigInvalid,
            $"{setting} names '{{{name}}}', which nothing here can fill in. "
            + $"The names are {string.Join(", ", known)}. "
            + "Write '{{' for a brace this tool should leave alone.");
    }
}
