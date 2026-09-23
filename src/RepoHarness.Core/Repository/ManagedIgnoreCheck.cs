using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Repository;

/// <summary>One rule of the managed block, with the paths it rules on and what it says about them.</summary>
/// <param name="Rule">The rule as the block writes it.</param>
/// <param name="Probes">
/// Paths the rule decides, relative to the tree's root with forward separators: the file the rule
/// names, or <see cref="AnyName"/> inside the directory it rules on - and, where that directory holds
/// directories of its own, a name inside one of those too, <see cref="AnyDirectoryName"/>: a rule
/// re-including them puts what they hold back in git while the directory's own answer stays the block's.
/// </param>
/// <param name="Ignores">Whether the rule keeps its probes out of git, rather than in it.</param>
public sealed record ManagedIgnoreRule(string Rule, IReadOnlyList<string> Probes, bool Ignores)
{
    /// <summary>The name a probe gives what a rule rules on without naming it.</summary>
    public const string AnyName = "dssharness-probe";

    /// <summary>
    /// The name a probe gives a directory it sits in that the rule does not name, apart from
    /// <see cref="AnyName"/>, so that no probe is ever a directory above another. A scratch repository
    /// makes every directory above a probe, as git takes each for one in the tree too; were a probe
    /// among them, a rule ending in <c>/</c> would match it there, where in the tree it matches no path
    /// but a directory.
    /// </summary>
    public const string AnyDirectoryName = "dssharness-folder";

    /// <summary><paramref name="probe"/> as a reader is shown it, a name the rule does not spell reading as &lt;any&gt;.</summary>
    /// <param name="probe">One of a rule's <see cref="Probes"/>.</param>
    public static string Shown(string probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return probe.Replace(AnyName, "<any>", StringComparison.Ordinal).Replace(AnyDirectoryName, "<any>", StringComparison.Ordinal);
    }
}

/// <summary>A rule that turns paths the managed block rules on the other way.</summary>
/// <param name="Source">The file holding the rule, as git names it - <c>.gitignore</c> for the tree's own.</param>
/// <param name="Line">Its line in that file, counting from one.</param>
/// <param name="Pattern">The rule as written.</param>
/// <param name="Paths">What it turns, each as <see cref="ManagedIgnoreRule.Shown"/> shows it.</param>
/// <param name="Wins">
/// Whether git follows it. A rule that wins undoes the block for those paths; one that loses does
/// nothing there, because another rule decides them - the block's own, one after it, one in an
/// ignore file nearer the paths, or one excluding a directory above them in any ignore file, since git
/// never looks inside an excluded directory.
/// </param>
/// <param name="Ignores">Whether the rule ignores the paths, where the block keeps them in git.</param>
public sealed record ManagedIgnoreConflict(
    string Source,
    int Line,
    string Pattern,
    IReadOnlyList<string> Paths,
    bool Wins,
    bool Ignores);

/// <summary>A path git would not say anything about, and why.</summary>
/// <param name="Path">The path, as <see cref="ManagedIgnoreRule.Shown"/> shows it.</param>
/// <param name="Why">Why, in git's own words: one beyond a symbolic link, which git never looks past.</param>
public sealed record ManagedIgnoreUnanswered(string Path, string Why);

/// <summary>What asking git about the paths the managed block rules on found.</summary>
/// <param name="Conflicts">Every rule that turns one of them the other way, the ones that win first, then in file and line order.</param>
/// <param name="Unruled">
/// Paths the block ignores and git does not, where git names no rule deciding them, neither in the
/// tree nor in a scratch repository holding the tree's own ignore files. Each as
/// <see cref="ManagedIgnoreRule.Shown"/> shows it.
/// </param>
/// <param name="Unanswered">Paths git would not answer about, while it answered about the rest.</param>
public sealed record ManagedIgnoreFindings(
    IReadOnlyList<ManagedIgnoreConflict> Conflicts,
    IReadOnlyList<string> Unruled,
    IReadOnlyList<ManagedIgnoreUnanswered> Unanswered)
{
    /// <summary>Nothing found.</summary>
    public static ManagedIgnoreFindings None { get; } = new([], [], []);
}

/// <summary>
/// Finds the ignore rules that turn a path the managed block rules on the other way, by asking git
/// which rule decides each one.
/// </summary>
/// <remarks>
/// Asked of git rather than read off the rules' spelling. What a rule matches depends on its anchor,
/// its wildcards and escapes, the directory its file is in, and whether a directory above the path is
/// already excluded, where no re-include reaches. A comparison of spellings got those wrong both ways:
/// it named a rule that matches nothing as overriding the block, and could not see a rule reaching a
/// managed path through a wildcard.
/// <para>
/// Asked of the tree as it is, git says which rule decides each path, and one deciding it against the
/// block undoes the block there. git never names a re-include that matched a directory above the
/// path, though - the path is then decided by no rule at all - and matches a rule ending in <c>/</c>
/// only against a directory that exists. So where the tree's answer is no rule, the question goes to
/// a scratch repository holding the tree's own ignore files, with every directory above the path
/// made: there git names the rule re-including the nearest of them. A path git will not answer about
/// - one beyond a symbolic link, which it never looks past - is said, and the rest are still answered.
/// </para>
/// <para>
/// Asked of the tree's own <c>.gitignore</c> with the block taken out, in a scratch repository holding
/// nothing else, git says which hand-written rule would decide each path on its own; one that would
/// decide it the other way, where the tree's answer is the block's, is overruled there. A rule
/// agreeing with the block is left alone whatever it spells: repeating the block changes nothing, and
/// a broad rule such as <c>.env</c> that happens to cover a managed path is not a second copy of the
/// block's rule for it.
/// </para>
/// <para>
/// An overruled rule is named as doing nothing there only where nothing the harness keeps in git rests
/// on it. Taken out of the tree's ignore files - its <c>.gitignore</c> files, its
/// <c>.git/info/exclude</c> and the excludes file its configuration names - in scratch repositories
/// asked with and without it, it must turn from kept to ignored no path the block rules on and no file
/// the harness keeps in git, and so no directory one of those is in. An allowlist that excludes
/// <c>/.harness-config/*</c>, or everything, re-includes each slot a placeholder is kept in - by the
/// slot's name, or by <c>!*/</c> - and git never looks inside an excluded directory: that re-include is
/// overruled for the slot's contents and needed for its placeholder, and is never told it does
/// nothing; nor is one the harness's configuration, an action's own files or an anchor registry rests
/// on. A re-include of a directory nothing excludes changes nothing, and is named.
/// </para>
/// </remarks>
public sealed class ManagedIgnoreCheck(IGitClient git, IFileSystem fileSystem, IHostPlatform platform, IHarnessOutput output)
{
    /// <summary>The name the tree's own ignore file has at its root, and the one git gives it.</summary>
    private const string GitIgnoreFileName = ".gitignore";

    /// <summary>What a scratch repository it cannot remove is warned about under: the command it serves.</summary>
    private const string CommandName = "init";

    private readonly IGitClient _git = git;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <summary>Every rule that turns a path <paramref name="rules"/> rules on the other way.</summary>
    /// <param name="root">The tree's root, whose <c>.gitignore</c> holds the managed block.</param>
    /// <param name="content">That <c>.gitignore</c> as it now is.</param>
    /// <param name="rules">The block's rules, each with the paths it is asked about.</param>
    /// <param name="kept">
    /// The files the harness keeps in git - its configuration, each placeholder, each file of an action's
    /// git keeps and a name standing for any action's, the anchor registries - relative to the tree's root
    /// with forward separators: no rule one of them rests on is told it does nothing.
    /// </param>
    /// <param name="cancellationToken">Stops the questions.</param>
    /// <exception cref="HarnessException">git could not answer, or its scratch repository could not be made.</exception>
    public async Task<ManagedIgnoreFindings> FindAsync(
        string root,
        string content,
        IReadOnlyList<ManagedIgnoreRule> rules,
        IReadOnlyList<string> kept,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(kept);

        var asked = rules.SelectMany(rule => rule.Probes.Select(probe => (Rule: rule, Probe: probe))).ToList();

        if (asked.Count == 0)
        {
            return ManagedIgnoreFindings.None;
        }

        var tree = await _git.ExplainIgnoredAsync(root, [.. asked.Select(entry => entry.Probe)], cancellationToken).ConfigureAwait(false);

        var found = new List<(IgnoreDecision Decision, ManagedIgnoreRule Rule, string Probe, bool Wins)>();
        var agreeing = new List<(ManagedIgnoreRule Rule, string Probe)>();
        var unnamed = new List<(ManagedIgnoreRule Rule, string Probe)>();
        var unanswered = new List<ManagedIgnoreUnanswered>();

        foreach (var ((rule, probe), decision) in asked.Zip(tree))
        {
            if (decision.Unanswered is { } why)
            {
                unanswered.Add(new ManagedIgnoreUnanswered(ManagedIgnoreRule.Shown(probe), why));
            }
            else if (decision.Ignored == rule.Ignores)
            {
                agreeing.Add((rule, probe));
            }
            else if (decision.Source is null)
            {
                // Not ignored, where the block ignores it, and by no rule git names: a directory above
                // it is re-included.
                unnamed.Add((rule, probe));
            }
            else
            {
                found.Add((decision, rule, probe, Wins: true));
            }
        }

        var unruled = new List<string>();

        using var scratch = new ScratchDirectory(_fileSystem, "ignore", why => _output.Warn(CommandName, why));

        // Asked of the tree only where a scratch repository holding its ignore files is made.
        var excludes = new Lazy<Task<TreeExcludes>>(() => ExcludesAsync(root, cancellationToken));

        if (agreeing.Count > 0)
        {
            var alone = await HandWrittenAsync(scratch, content, [.. agreeing.Select(entry => entry.Probe)], cancellationToken)
                .ConfigureAwait(false);
            var overruled = agreeing.Zip(alone)
                .Where(pair => pair.Second is { } own && own.Ignored != pair.First.Rule.Ignores)
                .Select(pair => (pair.First.Rule, pair.First.Probe, Own: pair.Second!))
                .ToList();
            var needed = await NeededAsync(
                    scratch,
                    root,
                    content,
                    excludes,
                    [.. overruled.Select(entry => entry.Own.Line).Distinct()],
                    [.. asked.Select(entry => entry.Probe).Concat(kept).Distinct(StringComparer.Ordinal)],
                    cancellationToken)
                .ConfigureAwait(false);

            // Overruled for these paths, a rule does nothing there only where nothing the harness keeps
            // rests on it: one it needs - an allowlist's re-include of the slot a placeholder is kept in
            // - would be deleted on the strength of the note, and the placeholder with it.
            found.AddRange(overruled
                .Where(entry => !needed.Contains(entry.Own.Line))
                .Select(entry => (entry.Own, entry.Rule, entry.Probe, Wins: false)));
        }

        if (unnamed.Count > 0)
        {
            var probes = unnamed.Select(entry => entry.Probe).ToList();
            var whole = await WholeAsync(scratch, "whole", root, content, await excludes.Value.ConfigureAwait(false), probes, cancellationToken)
                .ConfigureAwait(false);
            var reIncluded = await DecideAsync(scratch, whole, probes, cancellationToken).ConfigureAwait(false);

            foreach (var ((rule, probe), named) in unnamed.Zip(reIncluded))
            {
                if (named is { Ignored: false })
                {
                    found.Add((named, rule, probe, Wins: true));
                }
                else
                {
                    unruled.Add(ManagedIgnoreRule.Shown(probe));
                }
            }
        }

        return new ManagedIgnoreFindings(
            [.. found
                .GroupBy(entry => (Source: entry.Decision.Source!, entry.Decision.Line, Pattern: entry.Decision.Pattern!, entry.Wins))
                .OrderByDescending(group => group.Key.Wins)
                .ThenBy(group => group.Key.Source, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Line)
                .Select(group => new ManagedIgnoreConflict(
                    group.Key.Source,
                    group.Key.Line,
                    group.Key.Pattern,
                    [.. group.Select(entry => ManagedIgnoreRule.Shown(entry.Probe)).Distinct(StringComparer.Ordinal)],
                    group.Key.Wins,
                    Ignores: !group.First().Rule.Ignores))],
            [.. unruled.Distinct(StringComparer.Ordinal)],
            unanswered);
    }

    /// <summary>
    /// Which hand-written rule of <paramref name="content"/> would decide each probe were there no
    /// managed block, asked of a scratch repository holding that file and nothing else.
    /// </summary>
    /// <remarks>
    /// The block's lines are blanked rather than removed, so every other rule keeps the line number it
    /// has in the tree's file. Only that file's rules are answers: a rule from this machine's global
    /// excludes is not the tree's own, and is asked about in the tree.
    /// </remarks>
    private async Task<IReadOnlyList<IgnoreDecision?>> HandWrittenAsync(
        ScratchDirectory scratch,
        string content,
        IReadOnlyList<string> probes,
        CancellationToken cancellationToken)
    {
        var repository = await RepositoryAsync(scratch, "alone", GitIgnoreManager.WithoutManagedBlock(content), null, cancellationToken)
            .ConfigureAwait(false);

        var decisions = await DecideAsync(scratch, repository, probes, cancellationToken).ConfigureAwait(false);

        return [.. decisions.Select(decision => decision?.Source == GitIgnoreFileName ? decision : null)];
    }

    /// <summary>
    /// Of <paramref name="lines"/> of the tree's <c>.gitignore</c>, those something the harness keeps
    /// rests on: taken out, each turns a path in <paramref name="paths"/> from kept to ignored - asked of
    /// scratch repositories holding the tree's ignore files, with and without it.
    /// </summary>
    /// <remarks>
    /// Only a path taken from git counts: one taking it out would put in git - a file <c>*</c> hid from
    /// it - rested on nothing. A directory counts through the paths in it, as git answers a path below
    /// an excluded directory as ignored: every directory the block rules on holds a file the harness
    /// keeps, or is one the block excludes whatever else is written.
    /// </remarks>
    private async Task<HashSet<int>> NeededAsync(
        ScratchDirectory scratch,
        string root,
        string content,
        Lazy<Task<TreeExcludes>> excludes,
        IReadOnlyList<int> lines,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var needed = new HashSet<int>();

        if (lines.Count == 0)
        {
            return needed;
        }

        var tree = await excludes.Value.ConfigureAwait(false);
        var whole = await WholeAsync(scratch, "whole", root, content, tree, paths, cancellationToken).ConfigureAwait(false);
        var without = await WholeAsync(scratch, "without", root, content, tree, paths, cancellationToken).ConfigureAwait(false);

        MakeDirectories(scratch, whole, paths);
        MakeDirectories(scratch, without, paths);

        var with = await _git.ExplainIgnoredAsync(whole, paths, cancellationToken).ConfigureAwait(false);

        foreach (var line in lines)
        {
            InScratch(scratch, () => _fileSystem.WriteAllTextAtomic(Path.Combine(without, GitIgnoreFileName), WithoutLine(content, line)));

            var answers = await _git.ExplainIgnoredAsync(without, paths, cancellationToken).ConfigureAwait(false);

            if (with.Zip(answers).Any(pair => !pair.First.Ignored && pair.Second.Ignored))
            {
                needed.Add(line);
            }
        }

        return needed;
    }

    /// <summary><paramref name="content"/> with its line <paramref name="line"/>, counting from one, blanked, so every other keeps its number.</summary>
    private static string WithoutLine(string content, int line)
    {
        var lines = content.Split('\n');

        lines[line - 1] = string.Empty;

        return string.Join('\n', lines);
    }

    /// <summary>
    /// A scratch repository named <paramref name="name"/> holding the tree's ignore files: its
    /// <c>.gitignore</c> as <paramref name="gitIgnore"/> gives it, every <c>.gitignore</c> of its own on
    /// the way to each of <paramref name="paths"/>, and <paramref name="excludes"/>. Asked for again, it
    /// is the same repository, with what the later paths need added.
    /// </summary>
    /// <remarks>
    /// A nested file that is a link is left out, as git leaves it out of the tree. The files beside the
    /// <c>.gitignore</c> files rank below them, and still decide a path none of them does - one a rule
    /// being taken out leaves to them.
    /// </remarks>
    private async Task<string> WholeAsync(
        ScratchDirectory scratch,
        string name,
        string root,
        string gitIgnore,
        TreeExcludes excludes,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var repository = await RepositoryAsync(scratch, name, gitIgnore, excludes, cancellationToken).ConfigureAwait(false);

        InScratch(scratch, () =>
        {
            foreach (var directory in paths.SelectMany(Above).Distinct(StringComparer.Ordinal))
            {
                var own = Path.Combine(root, directory, GitIgnoreFileName);

                if (_fileSystem.FileExists(own) && !LinkPaths.IsLink(_fileSystem, own, _platform.PathComparison))
                {
                    _fileSystem.CreateDirectory(Path.Combine(repository, directory));
                    _fileSystem.WriteAllTextAtomic(Path.Combine(repository, directory, GitIgnoreFileName), _fileSystem.ReadAllText(own));
                }
            }
        });

        return repository;
    }

    /// <summary>
    /// The tree's ignore files beside its <c>.gitignore</c> files: its <c>.git/info/exclude</c> - its
    /// main checkout's, for a worktree, as git reads it - and the excludes file its configuration names.
    /// </summary>
    /// <exception cref="HarnessException">git would not say where they are, or the first could not be read.</exception>
    private async Task<TreeExcludes> ExcludesAsync(string root, CancellationToken cancellationToken)
    {
        var where = await _git.RunAsync(root, ["rev-parse", "--git-path", "info/exclude"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!where.Succeeded)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"git would not say where '{root}' keeps its info/exclude: {where.FailureMessage}");
        }

        var configured = await _git.RunAsync(root, ["config", "--path", "--get", "core.excludesFile"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // git config exits 1 where the name is not set, which is no failure: git then reads its default
        // excludes file, which every scratch repository reads as the tree does. Set to nothing, the name
        // has git read no excludes file at all, not the default one, and each scratch repository is set so.
        if (!configured.Succeeded && configured.ExitCode != 1)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"git would not say which excludes file '{root}' reads: {configured.FailureMessage}");
        }

        var info = Path.GetFullPath(Path.Combine(root, where.StandardOutput.Trim()));

        try
        {
            return new TreeExcludes(
                _fileSystem.FileExists(info) ? _fileSystem.ReadAllText(info) : null,
                !configured.Succeeded ? null
                : configured.StandardOutput.Trim() is { Length: > 0 } file ? Path.GetFullPath(Path.Combine(root, file))
                : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.Refused, $"'{info}' could not be read: {ex.Message.TrimEnd('.')}", ex);
        }
    }

    /// <summary>
    /// What decides each of <paramref name="probes"/> in the scratch repository at
    /// <paramref name="repository"/>, with every directory above each made: the rule git names for the
    /// probe, or, where it names none, the one re-including the nearest directory above it - a rule
    /// git never names for the path below, and one ending in <c>/</c> matches only once the directory
    /// exists. <see langword="null"/> where neither is there.
    /// </summary>
    /// <remarks>
    /// No probe is made a directory, as none is one in the tree: see <see cref="ManagedIgnoreRule.AnyDirectoryName"/>.
    /// </remarks>
    private async Task<IReadOnlyList<IgnoreDecision?>> DecideAsync(
        ScratchDirectory scratch,
        string repository,
        IReadOnlyList<string> probes,
        CancellationToken cancellationToken)
    {
        var directories = probes.SelectMany(Above).Distinct(StringComparer.Ordinal).ToList();

        MakeDirectories(scratch, repository, probes);

        var answers = await _git.ExplainIgnoredAsync(repository, [.. probes, .. directories], cancellationToken).ConfigureAwait(false);
        var byDirectory = directories.Zip(answers.Skip(probes.Count)).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);

        // The rule git names for the probe, or else for the nearest directory above it - which is then
        // one a rule re-includes: one a rule excluded would have excluded the probe too, and git names
        // the rule that did for every path below it.
        return [.. probes.Select((probe, index) => Above(probe)
            .Reverse()
            .Select(directory => byDirectory[directory])
            .Prepend(answers[index])
            .FirstOrDefault(Named))];

        static bool Named(IgnoreDecision decision) => decision is { Unanswered: null, Source: not null };
    }

    /// <summary>Makes every directory above each of <paramref name="paths"/> in the scratch repository at <paramref name="repository"/>.</summary>
    private void MakeDirectories(ScratchDirectory scratch, string repository, IEnumerable<string> paths)
        => InScratch(scratch, () =>
        {
            foreach (var directory in paths.SelectMany(Above).Distinct(StringComparer.Ordinal))
            {
                _fileSystem.CreateDirectory(Path.Combine(repository, directory));
            }
        });

    /// <summary>
    /// The repository in <paramref name="scratch"/> named <paramref name="name"/> - made the first time
    /// it is asked for, with <paramref name="excludes"/> where given - whose <c>.gitignore</c> now holds
    /// <paramref name="gitIgnore"/>.
    /// </summary>
    private async Task<string> RepositoryAsync(
        ScratchDirectory scratch,
        string name,
        string gitIgnore,
        TreeExcludes? excludes,
        CancellationToken cancellationToken)
    {
        var repository = Path.Combine(scratch.Path, name);

        if (!_fileSystem.DirectoryExists(Path.Combine(repository, ".git")))
        {
            InScratch(scratch, () => _fileSystem.CreateDirectory(repository));

            var created = await _git.RunAsync(repository, ["init", "--quiet"], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!created.Succeeded)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"Could not make a scratch repository to ask git about '{GitIgnoreFileName}': {created.FailureMessage}");
            }

            if (excludes?.InfoExclude is { } info)
            {
                InScratch(scratch, () =>
                {
                    _fileSystem.CreateDirectory(Path.Combine(repository, ".git", "info"));
                    _fileSystem.WriteAllTextAtomic(Path.Combine(repository, ".git", "info", "exclude"), info);
                });
            }

            if (excludes?.ExcludesFile is { } file)
            {
                var set = await _git.RunAsync(repository, ["config", "core.excludesFile", file], cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!set.Succeeded)
                {
                    throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"Could not give a scratch repository the tree's excludes file '{file}': {set.FailureMessage}");
                }
            }
        }

        InScratch(scratch, () => _fileSystem.WriteAllTextAtomic(Path.Combine(repository, GitIgnoreFileName), gitIgnore));

        return repository;
    }

    /// <summary>
    /// Does <paramref name="work"/> on a scratch repository, refused as git's answer being out of reach
    /// where it could not be done: a temporary directory this user cannot write, a disk that is full.
    /// </summary>
    private static void InScratch(ScratchDirectory scratch, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"the scratch repository in '{scratch.Path}' could not be written: {ex.Message.TrimEnd('.')}",
                ex);
        }
    }

    /// <summary>Every directory above <paramref name="probe"/>, outermost first, relative to the root with forward separators.</summary>
    private static IEnumerable<string> Above(string probe)
    {
        for (var slash = probe.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = probe.IndexOf('/', slash + 1))
        {
            yield return probe[..slash];
        }
    }

    /// <summary>The tree's ignore files beside its <c>.gitignore</c> files.</summary>
    /// <param name="InfoExclude">What its <c>.git/info/exclude</c> holds; <see langword="null"/> where it has none.</param>
    /// <param name="ExcludesFile">
    /// The excludes file its configuration names, as an absolute path; empty where its configuration sets
    /// the name to nothing, so git reads none; <see langword="null"/> where the name is not set, so git
    /// reads its default one.
    /// </param>
    private sealed record TreeExcludes(string? InfoExclude, string? ExcludesFile);
}
