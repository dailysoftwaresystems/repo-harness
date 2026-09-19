using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Repository;

/// <summary>One rule of the managed block, with a path it rules on and what it says about that path.</summary>
/// <param name="Rule">The rule as the block writes it.</param>
/// <param name="Probe">
/// A path the rule decides, relative to the tree's root with forward separators: the file the rule
/// names, or <see cref="AnyName"/> inside the directory it rules on.
/// </param>
/// <param name="Ignores">Whether the rule keeps <paramref name="Probe"/> out of git, rather than in it.</param>
public sealed record ManagedIgnoreRule(string Rule, string Probe, bool Ignores)
{
    /// <summary>The name a probe gives what a rule rules on without naming it.</summary>
    public const string AnyName = "dssharness-probe";

    /// <summary>The probe as a reader is shown it, a name the rule does not spell reading as &lt;any&gt;.</summary>
    public string Shown => Probe.Replace(AnyName, "<any>", StringComparison.Ordinal);
}

/// <summary>A rule that turns paths the managed block rules on the other way.</summary>
/// <param name="Source">
/// The file holding the rule, as git names it - <c>.gitignore</c> for the tree's own - or
/// <see langword="null"/> where no rule decides the paths at all.
/// </param>
/// <param name="Line">Its line in that file, counting from one.</param>
/// <param name="Pattern">The rule as written.</param>
/// <param name="Paths">What it turns, each as <see cref="ManagedIgnoreRule.Shown"/> shows it.</param>
/// <param name="Wins">
/// Whether git follows it. A rule that wins undoes the block for those paths; one that loses does
/// nothing there, because a rule after it decides them.
/// </param>
/// <param name="Ignores">Whether the rule ignores the paths, where the block keeps them in git.</param>
public sealed record ManagedIgnoreConflict(
    string? Source,
    int Line,
    string? Pattern,
    IReadOnlyList<string> Paths,
    bool Wins,
    bool Ignores);

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
/// Two questions. Asked of the tree as it is, git says which rule decides each path, and one deciding
/// it against the block undoes the block there. Asked of the tree's own <c>.gitignore</c> with the
/// block taken out, in a repository holding nothing else, git says which hand-written rule would
/// decide each path on its own; one that would decide it the other way, where the tree's answer is
/// the block's, does nothing there. A rule agreeing with the block is left alone whatever it spells:
/// repeating the block changes nothing, and a broad rule such as <c>.env</c> that happens to cover a
/// managed path is not a second copy of the block's rule for it.
/// </para>
/// </remarks>
public sealed class ManagedIgnoreCheck(IGitClient git, IFileSystem fileSystem)
{
    /// <summary>The name the tree's own ignore file has at its root, and the one git gives it.</summary>
    private const string GitIgnoreFileName = ".gitignore";

    private readonly IGitClient _git = git;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Every rule that turns a path <paramref name="rules"/> rules on the other way.</summary>
    /// <param name="root">The tree's root, whose <c>.gitignore</c> holds the managed block.</param>
    /// <param name="content">That <c>.gitignore</c> as it now is.</param>
    /// <param name="rules">The block's rules, each with the path it is asked about.</param>
    /// <param name="cancellationToken">Stops the questions.</param>
    /// <returns>One entry per rule, the ones that win first, then in file and line order.</returns>
    /// <exception cref="HarnessException">git could not answer.</exception>
    public async Task<IReadOnlyList<ManagedIgnoreConflict>> FindAsync(
        string root,
        string content,
        IReadOnlyList<ManagedIgnoreRule> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count == 0)
        {
            return [];
        }

        var probes = rules.Select(rule => rule.Probe).ToList();
        var tree = await _git.ExplainIgnoredAsync(root, probes, cancellationToken).ConfigureAwait(false);
        var alone = await HandWrittenAsync(content, probes, cancellationToken).ConfigureAwait(false);

        var found = new List<(IgnoreDecision Decision, ManagedIgnoreRule Rule, bool Wins)>();

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];

            if (tree[index].Ignored != rule.Ignores)
            {
                found.Add((tree[index], rule, Wins: true));
            }
            else if (alone[index] is { } own && own.Ignored != rule.Ignores)
            {
                found.Add((own, rule, Wins: false));
            }
        }

        return [.. found
            .GroupBy(entry => (entry.Decision.Source, entry.Decision.Line, entry.Decision.Pattern, entry.Wins))
            .OrderByDescending(group => group.Key.Wins)
            .ThenBy(group => group.Key.Source, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Line)
            .Select(group => new ManagedIgnoreConflict(
                group.Key.Source,
                group.Key.Line,
                group.Key.Pattern,
                [.. group.Select(entry => entry.Rule.Shown)],
                group.Key.Wins,
                Ignores: !group.First().Rule.Ignores))];
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
        string content,
        IReadOnlyList<string> probes,
        CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "dssharness-ignore-" + Guid.NewGuid().ToString("N"));

        try
        {
            _fileSystem.CreateDirectory(scratch);

            var created = await _git.RunAsync(scratch, ["init", "--quiet"], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!created.Succeeded)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"Could not make a scratch repository to ask git about '{GitIgnoreFileName}': {created.FailureMessage}");
            }

            _fileSystem.WriteAllTextAtomic(Path.Combine(scratch, GitIgnoreFileName), GitIgnoreManager.WithoutManagedBlock(content));

            var decisions = await _git.ExplainIgnoredAsync(scratch, probes, cancellationToken).ConfigureAwait(false);

            return [.. decisions.Select(decision => decision.Source == GitIgnoreFileName ? decision : null)];
        }
        finally
        {
            _fileSystem.DeleteDirectory(scratch);
        }
    }
}
