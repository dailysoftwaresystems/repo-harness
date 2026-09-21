using RepoHarness.Core.Commands;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// Which rules turn a path the managed block rules on the other way, as git itself decides those
/// paths - never as the rules happen to be spelled.
/// </summary>
public sealed class ManagedIgnoreCheckTests
{
    /// <summary>
    /// Each managed rule is asked about paths it decides itself: were a probe decided by another rule,
    /// or by none, every answer the check gives about that rule would describe a different one.
    /// </summary>
    [Fact]
    public async Task EveryManagedRule_DecidesItsOwnProbes_InAFreshlyInitialisedTree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);

        var rules = InitService.BuildIgnoreRules(new WorktreeSettings());
        var asked = rules.SelectMany(rule => rule.Probes.Select(probe => (Rule: rule, Probe: probe))).ToList();
        var decisions = await harness.GitClient.ExplainIgnoredAsync(temp.Path, [.. asked.Select(entry => entry.Probe)], token);

        Assert.All(asked.Zip(decisions), pair =>
        {
            Assert.Equal(".gitignore", pair.Second.Source);
            Assert.Equal(pair.First.Rule.Rule, pair.Second.Pattern);
            Assert.Equal(pair.First.Rule.Ignores, pair.Second.Ignored);
        });

        var findings = await Check(harness).FindAsync(temp.Path, File.ReadAllText(temp.Combine(".gitignore")), rules, token);

        Assert.Empty(findings.Conflicts);
        Assert.Empty(findings.Unruled);
        Assert.Empty(findings.Unanswered);
    }

    /// <summary>
    /// A rule after the block that re-includes a slot's contents wins, and puts a host's connection
    /// data back in reach of git add: named, as the rule git follows, for a file there and one in a
    /// host's directory alike.
    /// </summary>
    [Fact]
    public async Task AReIncludeAfterTheBlock_Wins_AndIsNamed()
    {
        var (findings, _) = await FindAsync(after: "!/.harness-config/sshItems/*\n");

        var conflict = Assert.Single(findings.Conflicts);

        Assert.True(conflict.Wins);
        Assert.False(conflict.Ignores);
        Assert.Equal(".gitignore", conflict.Source);
        Assert.Equal("!/.harness-config/sshItems/*", conflict.Pattern);
        Assert.Equal([".harness-config/sshItems/<any>", ".harness-config/sshItems/<any>/<any>"], conflict.Paths);
    }

    /// <summary>
    /// A rule re-including each host's directory leaves a file in the slot to the block, and puts
    /// every key the host directories hold back in reach of git add. git names no rule for a file
    /// below a re-included directory, so the rule is found where the directory exists, and named at
    /// the line the tree's file has it on.
    /// </summary>
    [Fact]
    public async Task AReIncludeOfEveryHostsDirectory_Wins_AndIsNamed()
    {
        var (findings, content) = await FindAsync(after: "!/.harness-config/sshItems/*/\n");

        var conflict = Assert.Single(findings.Conflicts);

        Assert.True(conflict.Wins);
        Assert.False(conflict.Ignores);
        Assert.Equal(".gitignore", conflict.Source);
        Assert.Equal(LineOf(content, "!/.harness-config/sshItems/*/"), conflict.Line);
        Assert.Equal("!/.harness-config/sshItems/*/", conflict.Pattern);
        Assert.Equal([".harness-config/sshItems/<any>/<any>"], conflict.Paths);
        Assert.Empty(findings.Unruled);
    }

    /// <summary>
    /// Re-including the runs directory as a directory is named in the tree's own file, where it is,
    /// rather than reported as no rule at all and blamed on another ignore file.
    /// </summary>
    [Fact]
    public async Task AReIncludeOfTheRunsDirectory_IsNamedInTheTreesOwnFile()
    {
        var (findings, content) = await FindAsync(after: "!/.harness-config/runs/\n");

        var conflict = Assert.Single(findings.Conflicts);

        Assert.True(conflict.Wins);
        Assert.Equal(".gitignore", conflict.Source);
        Assert.Equal(LineOf(content, "!/.harness-config/runs/"), conflict.Line);
        Assert.Equal("!/.harness-config/runs/", conflict.Pattern);
        Assert.Equal([".harness-config/runs/<any>"], conflict.Paths);
        Assert.Empty(findings.Unruled);
    }

    /// <summary>The same re-include in a nested ignore file is named in that file.</summary>
    [Fact]
    public async Task AReIncludeOfADirectory_InANestedIgnoreFile_IsNamedInThatFile()
    {
        var (findings, _) = await FindAsync(nested: "# the runs\n!/runs/\n");

        var conflict = Assert.Single(findings.Conflicts);

        Assert.True(conflict.Wins);
        Assert.Equal(".harness-config/.gitignore", conflict.Source);
        Assert.Equal(2, conflict.Line);
        Assert.Equal("!/runs/", conflict.Pattern);
        Assert.Equal([".harness-config/runs/<any>"], conflict.Paths);
    }

    /// <summary>
    /// A rule before the block that re-includes a directory the block ignores is overruled by it:
    /// named as doing nothing there, though git names no rule for the files below it either.
    /// </summary>
    [Fact]
    public async Task AReIncludeOfADirectoryBeforeTheBlock_IsNamedAsDoingNothing()
    {
        var (findings, _) = await FindAsync(before: "!/.harness-config/runs/\n");

        var conflict = Assert.Single(findings.Conflicts);

        Assert.False(conflict.Wins);
        Assert.False(conflict.Ignores);
        Assert.Equal(1, conflict.Line);
        Assert.Equal("!/.harness-config/runs/", conflict.Pattern);
    }

    /// <summary>
    /// The same rule before the block is overruled by it: named as doing nothing there, at the line
    /// the file has it on.
    /// </summary>
    [Fact]
    public async Task AReIncludeBeforeTheBlock_IsOverruled_AndNamedAsDoingNothing()
    {
        var (findings, _) = await FindAsync(before: "bin/\n!/.harness-config/sshItems/*\n");

        var conflict = Assert.Single(findings.Conflicts);

        Assert.False(conflict.Wins);
        Assert.Equal(2, conflict.Line);
        Assert.Equal("!/.harness-config/sshItems/*", conflict.Pattern);
    }

    /// <summary>
    /// A whole-directory rule excludes every placeholder the block re-includes inside it, because git
    /// re-includes nothing from an excluded directory - however late the block comes. Named once,
    /// with every placeholder it takes.
    /// </summary>
    [Fact]
    public async Task AWholeDirectoryRule_TakesEveryPlaceholder_WhereverTheBlockIs()
    {
        var (findings, _) = await FindAsync(before: "/.harness-config/\n");

        var conflict = Assert.Single(findings.Conflicts);

        Assert.True(conflict.Wins);
        Assert.True(conflict.Ignores);
        Assert.Equal(1, conflict.Line);
        Assert.Equal(
            [
                ".harness-config/sshItems/.gitkeep",
                ".harness-config/wslDistros/.gitkeep",
                ".harness-config/runner/.env/.gitkeep",
                ".harness-config/runner/.secrets/.gitkeep",
            ],
            conflict.Paths);
    }

    /// <summary>
    /// Rules that turn nothing the block rules on are never named, whatever they spell: one matching
    /// nothing at all - a leading space and an escaped '!' make it a literal name - one repeating the
    /// block, and broad ones covering managed paths the same way the block does.
    /// </summary>
    [Theory]
    [InlineData(" \\!/.harness-config/sshItems/\n")]
    [InlineData(".harness-config/worktrees/\n/.harness-config/sshItems/*\n!/.harness-config/sshItems/.gitkeep\n")]
    [InlineData("*.json\n**/build/\n")]
    public async Task ARuleThatTurnsNothing_IsNeverNamed(string handWritten)
    {
        var (before, _) = await FindAsync(before: handWritten);
        var (after, _) = await FindAsync(after: handWritten);

        Assert.Empty(before.Conflicts);
        Assert.Empty(before.Unruled);
        Assert.Empty(after.Conflicts);
        Assert.Empty(after.Unruled);
    }

    /// <summary>
    /// A broad rule taking a placeholder's whole directory is named, though it spells nothing of the
    /// harness's: '.env', which so many repositories ignore, is also the name of the directory the
    /// runner's values live in, and git re-includes nothing from a directory it excludes.
    /// </summary>
    [Fact]
    public async Task ABroadRuleTakingAPlaceholdersDirectory_IsNamed()
    {
        var (findings, _) = await FindAsync(before: "node_modules/\n.env\n");

        var conflict = Assert.Single(findings.Conflicts);

        Assert.True(conflict.Wins);
        Assert.True(conflict.Ignores);
        Assert.Equal(2, conflict.Line);
        Assert.Equal([".harness-config/runner/.env/.gitkeep"], conflict.Paths);
    }

    /// <summary>
    /// A rule in another ignore file decides too, and is named in that file: a nested .gitignore
    /// outranks the tree's own for the paths below it.
    /// </summary>
    [Fact]
    public async Task ARuleInANestedIgnoreFile_IsNamedInThatFile()
    {
        var (findings, _) = await FindAsync(nested: "!*\n");

        Assert.NotEmpty(findings.Conflicts);
        Assert.All(findings.Conflicts, conflict =>
        {
            Assert.True(conflict.Wins);
            Assert.Equal(".harness-config/.gitignore", conflict.Source);
            Assert.Equal("!*", conflict.Pattern);
        });
    }

    /// <summary>
    /// A path beyond a symbolic link - a runs directory kept on another disk - makes git refuse to
    /// answer about it, and it alone: that one is said, with git's reason, and a rule undoing the
    /// block elsewhere is still named.
    /// </summary>
    [Fact]
    public async Task APathBeyondALink_IsSaidUnanswered_AndTheRestAreStillNamed()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);
        File.AppendAllText(temp.Combine(".gitignore"), "!/.harness-config/sshItems/*\n");
        Link(temp.Combine(".harness-config", "runs"), elsewhere.Path);

        var findings = await Check(harness).FindAsync(
            temp.Path,
            File.ReadAllText(temp.Combine(".gitignore")),
            InitService.BuildIgnoreRules(new WorktreeSettings()),
            token);

        var unanswered = Assert.Single(findings.Unanswered);
        Assert.Equal(".harness-config/runs/<any>", unanswered.Path);
        Assert.Contains("symbolic link", unanswered.Why, StringComparison.Ordinal);

        var conflict = Assert.Single(findings.Conflicts);
        Assert.Equal("!/.harness-config/sshItems/*", conflict.Pattern);
    }

    /// <summary>
    /// A runs directory and a worktrees root kept on another disk through a link are ignored as the
    /// links they are: a rule ending in '/' matches only a directory, and git status listed each link,
    /// for git add to commit.
    /// </summary>
    [Fact]
    public async Task ALinkedRunsDirectoryAndWorktreesRoot_AreIgnored()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);
        await harness.CommitAllAsync(temp.Path, "harness", token);

        Link(temp.Combine(".harness-config", "runs"), Directory.CreateDirectory(Path.Combine(elsewhere.Path, "runs")).FullName);
        Link(temp.Combine(".harness-config", "worktrees"), Directory.CreateDirectory(Path.Combine(elsewhere.Path, "worktrees")).FullName);

        var status = await harness.RunGitAsync(temp.Path, ["status", "--porcelain", "--untracked-files=all"], token);

        Assert.True(status.Succeeded, status.FailureMessage);
        Assert.Equal(string.Empty, status.StandardOutput.Trim());
    }

    /// <summary>
    /// Only the tree's own file answers the second question. A rule from this machine's global
    /// excludes that decides a managed path in the scratch repository is not the tree's, and is never
    /// named as one the block overrules; and the scratch repository is gone once the check is done.
    /// </summary>
    [Fact]
    public async Task ARuleFromOutsideTheTreesOwnFile_IsNeverNamedAsOverruled_AndTheScratchIsRemoved()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);

        string? scratch = null;

        var git = new InterceptingGitClient(harness.GitClient)
        {
            AfterExplainIgnored = (directory, decisions) =>
            {
                if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(temp.Path), StringComparison.OrdinalIgnoreCase))
                {
                    return decisions;
                }

                // Every path decided the other way, by a rule of the machine's global excludes.
                scratch = directory;

                return [.. decisions.Select(decision => decision with
                {
                    Source = "/home/dev/.config/git/ignore",
                    Line = 1,
                    Pattern = decision.Ignored ? "!*" : "*",
                })];
            },
        };

        var findings = await new ManagedIgnoreCheck(git, harness.FileSystem, harness.Platform, harness.Output).FindAsync(
            temp.Path,
            File.ReadAllText(temp.Combine(".gitignore")),
            InitService.BuildIgnoreRules(new WorktreeSettings()),
            token);

        Assert.Empty(findings.Conflicts);
        Assert.NotNull(scratch);
        Assert.False(Directory.Exists(scratch));
    }

    /// <summary>
    /// A scratch repository that cannot be written - a temporary directory this user cannot write, a
    /// disk that is full - is refused as git's answer being out of reach, naming the directory: init
    /// says so in a note and carries on, rather than failing as a defect in this tool.
    /// </summary>
    [Fact]
    public async Task AScratchRepositoryThatCannotBeWritten_IsRefused_NamingIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);

        var check = new ManagedIgnoreCheck(harness.GitClient, new ScratchFileSystem(harness.FileSystem) { Unwritable = true }, harness.Platform, harness.Output);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => check.FindAsync(
            temp.Path,
            File.ReadAllText(temp.Combine(".gitignore")),
            InitService.BuildIgnoreRules(new WorktreeSettings()),
            token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith("the scratch repository in '", refused.Message, StringComparison.Ordinal);
        Assert.Contains("dssharness-ignore-", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith($"' could not be written: {ScratchFileSystem.Said.TrimEnd('.')}", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scratch repository that cannot be removed afterwards - a scanner holding a file in it - is
    /// warned about, naming it, and the answer stands.
    /// </summary>
    [Fact]
    public async Task AScratchRepositoryThatCannotBeRemoved_IsWarnedAbout_AndTheAnswerStands()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        var fileSystem = new ScratchFileSystem(harness.FileSystem) { Unremovable = true };

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);
        File.AppendAllText(temp.Combine(".gitignore"), "!/.harness-config/sshItems/*\n");

        try
        {
            var findings = await new ManagedIgnoreCheck(harness.GitClient, fileSystem, harness.Platform, harness.Output).FindAsync(
                temp.Path,
                File.ReadAllText(temp.Combine(".gitignore")),
                InitService.BuildIgnoreRules(new WorktreeSettings()),
                token);

            Assert.Equal("!/.harness-config/sshItems/*", Assert.Single(findings.Conflicts).Pattern);
            Assert.Contains($"'{fileSystem.Kept}' could not be removed: {ScratchFileSystem.Said}", harness.StandardError.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (fileSystem.Kept is { } kept)
            {
                harness.FileSystem.DeleteDirectory(kept);
            }
        }
    }

    /// <summary>
    /// init names the rule as a note, and leaves it where it is: hand-written rules are the
    /// repository's own.
    /// </summary>
    [Fact]
    public async Task Init_NamesARuleThatWins_AndOneThatDoesNothing_AndLeavesBoth()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", "bin/\n!/.harness-config/runner/.env/*\n");

        var first = await harness.InitService.InitializeAsync(temp.Path, token);
        File.AppendAllText(temp.Combine(".gitignore"), "/.harness-config/runner/.env/.gitkeep\n");
        var second = await harness.InitService.InitializeAsync(temp.Path, token);

        Assert.True(first.Succeeded, first.Message);

        var overruled = Assert.Single(first.Details!, line => line.StartsWith("note", StringComparison.Ordinal));
        Assert.Equal(
            "note    .gitignore line 2 ('!/.harness-config/runner/.env/*') re-includes '.harness-config/runner/.env/<any>', "
            + "'.harness-config/runner/.env/<any>/<any>', which the managed block ignores; a later rule decides them, so this "
            + "one does nothing there",
            overruled);

        var notes = second.Details!.Where(line => line.StartsWith("note", StringComparison.Ordinal)).ToList();
        var lines = File.ReadAllLines(temp.Combine(".gitignore"));
        var last = lines.Length;

        Assert.Equal(2, notes.Count);
        Assert.Equal(
            $"note    .gitignore line {last} ('/.harness-config/runner/.env/.gitkeep') ignores '.harness-config/runner/.env/.gitkeep', "
            + "which the managed block keeps in git; git follows that rule",
            notes[0]);
        Assert.Contains("line 2 ('!/.harness-config/runner/.env/*')", notes[1], StringComparison.Ordinal);
        Assert.Equal("!/.harness-config/runner/.env/*", lines[1]);
    }

    /// <summary>init names a path git would not answer about with git's reason, and still names the rest.</summary>
    [Fact]
    public async Task Init_NamesAPathBeyondALink_AndStillNamesTheRest()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);

        File.AppendAllText(temp.Combine(".gitignore"), "!/.harness-config/sshItems/*/\n");
        Link(temp.Combine(".harness-config", "runs"), elsewhere.Path);

        var outcome = await harness.InitService.InitializeAsync(temp.Path, token);

        Assert.True(outcome.Succeeded, outcome.Message);

        var notes = outcome.Details!.Where(line => line.StartsWith("note", StringComparison.Ordinal)).ToList();

        Assert.Contains(
            notes,
            note => note.StartsWith("note    git would not say which rule decides '.harness-config/runs/<any>': ", StringComparison.Ordinal)
                && note.Contains("symbolic link", StringComparison.Ordinal));
        Assert.Contains(
            notes,
            note => note.Contains("('!/.harness-config/sshItems/*/') re-includes '.harness-config/sshItems/<any>/<any>'", StringComparison.Ordinal)
                && note.EndsWith("git follows that rule", StringComparison.Ordinal));
    }

    private static ManagedIgnoreCheck Check(HarnessFactory harness) => new(harness.GitClient, harness.FileSystem, harness.Platform, harness.Output);

    /// <summary>The line <paramref name="rule"/> is on in <paramref name="content"/>, counting from one: its last.</summary>
    private static int LineOf(string content, string rule)
        => Array.LastIndexOf(content.Split('\n'), rule) + 1;

    /// <summary>A symbolic link at <paramref name="path"/> to the directory <paramref name="target"/>, or the test skipped where this machine allows none.</summary>
    private static void Link(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }
    }

    /// <summary>
    /// Initialises a tree whose .gitignore holds <paramref name="before"/> ahead of the block and
    /// <paramref name="after"/> behind it, with <paramref name="nested"/> as .harness-config's own
    /// .gitignore when given, and asks which rules turn a managed path.
    /// </summary>
    private static async Task<(ManagedIgnoreFindings Findings, string Content)> FindAsync(
        string before = "",
        string after = "",
        string? nested = null)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);

        if (before.Length > 0)
        {
            temp.WriteFile(".gitignore", before);
        }

        await harness.InitService.InitializeAsync(temp.Path, token);

        if (after.Length > 0)
        {
            File.AppendAllText(temp.Combine(".gitignore"), after);
        }

        if (nested is not null)
        {
            temp.WriteFile(Path.Combine(".harness-config", ".gitignore"), nested);
        }

        var content = File.ReadAllText(temp.Combine(".gitignore"));
        var findings = await Check(harness).FindAsync(temp.Path, content, InitService.BuildIgnoreRules(new WorktreeSettings()), token);

        return (findings, content);
    }

    /// <summary>
    /// A file system whose scratch repositories cannot be made, or cannot be removed, as where the
    /// temporary directory cannot be written, or a scanner holds a file in one.
    /// </summary>
    private sealed class ScratchFileSystem(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public const string Said = "Access to the path is denied.";

        /// <summary>Whether a scratch repository cannot be made.</summary>
        public bool Unwritable { get; init; }

        /// <summary>Whether a scratch repository cannot be removed.</summary>
        public bool Unremovable { get; init; }

        /// <summary>The scratch directory left behind, for the test to remove.</summary>
        public string? Kept { get; private set; }

        public override void CreateDirectory(string path)
        {
            if (Unwritable && IsScratch(path))
            {
                throw new UnauthorizedAccessException(Said);
            }

            base.CreateDirectory(path);
        }

        public override void DeleteDirectory(string path)
        {
            if (Unremovable && IsScratch(path))
            {
                Kept = path;
                throw new IOException(Said);
            }

            base.DeleteDirectory(path);
        }

        private static bool IsScratch(string path) => path.Contains("dssharness-ignore-", StringComparison.Ordinal);
    }
}
