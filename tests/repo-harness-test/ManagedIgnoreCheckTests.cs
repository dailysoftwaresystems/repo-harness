using RepoHarness.Core.Commands;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Repository;

namespace RepoHarness.Tests;

/// <summary>
/// Which rules turn a path the managed block rules on the other way, as git itself decides those
/// paths - never as the rules happen to be spelled.
/// </summary>
public sealed class ManagedIgnoreCheckTests
{
    /// <summary>
    /// Each managed rule is asked about a path it decides itself: were a probe decided by another rule,
    /// or by none, every answer the check gives about that rule would describe a different one.
    /// </summary>
    [Fact]
    public async Task EveryManagedRule_DecidesItsOwnProbe_InAFreshlyInitialisedTree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);

        var rules = InitService.BuildIgnoreRules(new WorktreeSettings());
        var decisions = await harness.GitClient.ExplainIgnoredAsync(temp.Path, [.. rules.Select(rule => rule.Probe)], token);

        Assert.All(rules.Zip(decisions), pair =>
        {
            Assert.Equal(".gitignore", pair.Second.Source);
            Assert.Equal(pair.First.Rule, pair.Second.Pattern);
            Assert.Equal(pair.First.Ignores, pair.Second.Ignored);
        });

        Assert.Empty(await Check(harness).FindAsync(temp.Path, File.ReadAllText(temp.Combine(".gitignore")), rules, token));
    }

    /// <summary>
    /// A rule after the block that re-includes a slot's contents wins, and puts a host's connection
    /// data back in reach of git add: named, as the rule git follows.
    /// </summary>
    [Fact]
    public async Task AReIncludeAfterTheBlock_Wins_AndIsNamed()
    {
        var (conflicts, _) = await FindAsync(after: "!/.harness-config/sshItems/*\n");

        var conflict = Assert.Single(conflicts);

        Assert.True(conflict.Wins);
        Assert.False(conflict.Ignores);
        Assert.Equal(".gitignore", conflict.Source);
        Assert.Equal("!/.harness-config/sshItems/*", conflict.Pattern);
        Assert.Equal([".harness-config/sshItems/<any>"], conflict.Paths);
    }

    /// <summary>
    /// The same rule before the block is overruled by it: named as doing nothing there, at the line
    /// the file has it on.
    /// </summary>
    [Fact]
    public async Task AReIncludeBeforeTheBlock_IsOverruled_AndNamedAsDoingNothing()
    {
        var (conflicts, _) = await FindAsync(before: "bin/\n!/.harness-config/sshItems/*\n");

        var conflict = Assert.Single(conflicts);

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
        var (conflicts, _) = await FindAsync(before: "/.harness-config/\n");

        var conflict = Assert.Single(conflicts);

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

        Assert.Empty(before);
        Assert.Empty(after);
    }

    /// <summary>
    /// A broad rule taking a placeholder's whole directory is named, though it spells nothing of the
    /// harness's: '.env', which so many repositories ignore, is also the name of the directory the
    /// runner's values live in, and git re-includes nothing from a directory it excludes.
    /// </summary>
    [Fact]
    public async Task ABroadRuleTakingAPlaceholdersDirectory_IsNamed()
    {
        var (conflicts, _) = await FindAsync(before: "node_modules/\n.env\n");

        var conflict = Assert.Single(conflicts);

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
        var (conflicts, _) = await FindAsync(nested: "!*\n");

        Assert.NotEmpty(conflicts);
        Assert.All(conflicts, conflict =>
        {
            Assert.True(conflict.Wins);
            Assert.Equal(".harness-config/.gitignore", conflict.Source);
            Assert.Equal("!*", conflict.Pattern);
        });
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

        var conflicts = await new ManagedIgnoreCheck(git, harness.FileSystem).FindAsync(
            temp.Path,
            File.ReadAllText(temp.Combine(".gitignore")),
            InitService.BuildIgnoreRules(new WorktreeSettings()),
            token);

        Assert.Empty(conflicts);
        Assert.NotNull(scratch);
        Assert.False(Directory.Exists(scratch));
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
            + "which the managed block ignores; a later rule decides them, so this one does nothing there",
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

    private static ManagedIgnoreCheck Check(HarnessFactory harness) => new(harness.GitClient, harness.FileSystem);

    /// <summary>
    /// Initialises a tree whose .gitignore holds <paramref name="before"/> ahead of the block and
    /// <paramref name="after"/> behind it, with <paramref name="nested"/> as .harness-config's own
    /// .gitignore when given, and asks which rules turn a managed path.
    /// </summary>
    private static async Task<(IReadOnlyList<ManagedIgnoreConflict> Conflicts, string Content)> FindAsync(
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
        var conflicts = await Check(harness).FindAsync(temp.Path, content, InitService.BuildIgnoreRules(new WorktreeSettings()), token);

        return (conflicts, content);
    }
}
