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
/// directories of its own, a name inside one of those too: a rule re-including them puts what they
/// hold back in git while the directory's own answer stays the block's.
/// </param>
/// <param name="Ignores">Whether the rule keeps its probes out of git, rather than in it.</param>
public sealed record ManagedIgnoreRule(string Rule, IReadOnlyList<string> Probes, bool Ignores)
{
    /// <summary>The name a probe gives what a rule rules on without naming it.</summary>
    public const string AnyName = "dssharness-probe";

    /// <summary><paramref name="probe"/> as a reader is shown it, a name the rule does not spell reading as &lt;any&gt;.</summary>
    /// <param name="probe">One of a rule's <see cref="Probes"/>.</param>
    public static string Shown(string probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return probe.Replace(AnyName, "<any>", StringComparison.Ordinal);
    }
}

/// <summary>A rule that turns paths the managed block rules on the other way.</summary>
/// <param name="Source">The file holding the rule, as git names it - <c>.gitignore</c> for the tree's own.</param>
/// <param name="Line">Its line in that file, counting from one.</param>
/// <param name="Pattern">The rule as written.</param>
/// <param name="Paths">What it turns, each as <see cref="ManagedIgnoreRule.Shown"/> shows it.</param>
/// <param name="Wins">
/// Whether git follows it. A rule that wins undoes the block for those paths; one that loses does
/// nothing there, because a rule after it decides them.
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
/// tree nor in a scratch repository holding the tree's own ignore files along the way. Each as
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
/// a scratch repository holding the tree's own ignore files along the path, with every directory
/// above it made: there git names the rule re-including the nearest of them. A path git will not
/// answer about - one beyond a symbolic link, which it never looks past - is said, and the rest are
/// still answered.
/// </para>
/// <para>
/// Asked of the tree's own <c>.gitignore</c> with the block taken out, in a scratch repository holding
/// nothing else, git says which hand-written rule would decide each path on its own; one that would
/// decide it the other way, where the tree's answer is the block's, does nothing there. A rule
/// agreeing with the block is left alone whatever it spells: repeating the block changes nothing, and
/// a broad rule such as <c>.env</c> that happens to cover a managed path is not a second copy of the
/// block's rule for it.
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
    /// <param name="cancellationToken">Stops the questions.</param>
    /// <exception cref="HarnessException">git could not answer, or its scratch repository could not be made.</exception>
    public async Task<ManagedIgnoreFindings> FindAsync(
        string root,
        string content,
        IReadOnlyList<ManagedIgnoreRule> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(rules);

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

        if (agreeing.Count > 0)
        {
            var alone = await HandWrittenAsync(scratch, content, [.. agreeing.Select(entry => entry.Probe)], cancellationToken)
                .ConfigureAwait(false);

            foreach (var ((rule, probe), own) in agreeing.Zip(alone))
            {
                if (own is not null && own.Ignored != rule.Ignores)
                {
                    found.Add((own, rule, probe, Wins: false));
                }
            }
        }

        if (unnamed.Count > 0)
        {
            var reIncluded = await ReIncludedAsync(scratch, root, content, [.. unnamed.Select(entry => entry.Probe)], cancellationToken)
                .ConfigureAwait(false);

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
        var repository = await RepositoryAsync(scratch, "alone", GitIgnoreManager.WithoutManagedBlock(content), cancellationToken)
            .ConfigureAwait(false);

        var decisions = await DecideAsync(scratch, repository, probes, cancellationToken).ConfigureAwait(false);

        return [.. decisions.Select(decision => decision?.Source == GitIgnoreFileName ? decision : null)];
    }

    /// <summary>
    /// The rule re-including a directory above each probe, asked of a scratch repository holding the
    /// tree's <c>.gitignore</c> and every <c>.gitignore</c> of its own along the way; <see langword="null"/>
    /// where no such rule is there.
    /// </summary>
    /// <remarks>
    /// Those files outrank <c>.git/info/exclude</c> and every excludes file a configuration names, so a
    /// re-include undoing the block is in one of them. One that is a link is left out, as git leaves
    /// it out of the tree.
    /// </remarks>
    private async Task<IReadOnlyList<IgnoreDecision?>> ReIncludedAsync(
        ScratchDirectory scratch,
        string root,
        string content,
        IReadOnlyList<string> probes,
        CancellationToken cancellationToken)
    {
        var repository = await RepositoryAsync(scratch, "whole", content, cancellationToken).ConfigureAwait(false);

        InScratch(scratch, () =>
        {
            foreach (var directory in probes.SelectMany(Above).Distinct(StringComparer.Ordinal))
            {
                var own = Path.Combine(root, directory, GitIgnoreFileName);

                if (_fileSystem.FileExists(own) && !LinkPaths.IsLink(_fileSystem, own, _platform.PathComparison))
                {
                    _fileSystem.CreateDirectory(Path.Combine(repository, directory));
                    _fileSystem.WriteAllTextAtomic(Path.Combine(repository, directory, GitIgnoreFileName), _fileSystem.ReadAllText(own));
                }
            }
        });

        return await DecideAsync(scratch, repository, probes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What decides each of <paramref name="probes"/> in the scratch repository at
    /// <paramref name="repository"/>, with every directory above each made: the rule git names for the
    /// probe, or, where it names none, the one re-including the nearest directory above it - a rule
    /// git never names for the path below, and one ending in <c>/</c> matches only once the directory
    /// exists. <see langword="null"/> where neither is there.
    /// </summary>
    private async Task<IReadOnlyList<IgnoreDecision?>> DecideAsync(
        ScratchDirectory scratch,
        string repository,
        IReadOnlyList<string> probes,
        CancellationToken cancellationToken)
    {
        var directories = probes.SelectMany(Above).Distinct(StringComparer.Ordinal).ToList();

        InScratch(scratch, () =>
        {
            foreach (var directory in directories)
            {
                _fileSystem.CreateDirectory(Path.Combine(repository, directory));
            }
        });

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

    /// <summary>A fresh repository in <paramref name="scratch"/>, named <paramref name="name"/>, whose <c>.gitignore</c> holds <paramref name="gitIgnore"/>.</summary>
    private async Task<string> RepositoryAsync(ScratchDirectory scratch, string name, string gitIgnore, CancellationToken cancellationToken)
    {
        var repository = Path.Combine(scratch.Path, name);

        InScratch(scratch, () => _fileSystem.CreateDirectory(repository));

        var created = await _git.RunAsync(repository, ["init", "--quiet"], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!created.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not make a scratch repository to ask git about '{GitIgnoreFileName}': {created.FailureMessage}");
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
}
