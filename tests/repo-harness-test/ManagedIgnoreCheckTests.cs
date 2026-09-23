using RepoHarness.Core.Anchors;
using RepoHarness.Core.Commands;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Processes;
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

        var findings = await Check(harness).FindAsync(temp.Path, File.ReadAllText(temp.Combine(".gitignore")), rules, InitService.KeptInGit([], []), token);

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
    /// An allowlist excludes each slot the block keeps a placeholder in and re-includes it, which the
    /// placeholder rests on, since git never looks inside an excluded directory. Each re-include is
    /// overruled for its slot's contents and needed for its placeholder, so none is named as doing
    /// nothing - the one keeping the directory an action's own files are in neither, though the block
    /// rules there only on what an action's steps write. Without a slot's re-include, the rule
    /// excluding the slot is named, taking its placeholder - under .harness-config, and under the
    /// runner the allowlist excludes again.
    /// </summary>
    [Theory]
    [InlineData("", "", "")]
    [InlineData("!/.harness-config/sshItems/\n", "/.harness-config/*", ".harness-config/sshItems/.gitkeep")]
    [InlineData("!/.harness-config/runner/.env/\n", "/.harness-config/runner/*", ".harness-config/runner/.env/.gitkeep")]
    public async Task AnAllowlistsReIncludeOfASlot_IsLeftAlone_ForThePlaceholderRestsOnIt(string removed, string taking, string placeholder)
    {
        var (findings, _) = await FindAsync(before: removed.Length > 0 ? Allowlist.Replace(removed, string.Empty, StringComparison.Ordinal) : Allowlist);

        Assert.Empty(findings.Unruled);

        if (removed.Length == 0)
        {
            Assert.Empty(findings.Conflicts);
            return;
        }

        var conflict = Assert.Single(findings.Conflicts);

        Assert.True(conflict.Wins);
        Assert.True(conflict.Ignores);
        Assert.Equal(taking, conflict.Pattern);
        Assert.Equal([placeholder], conflict.Paths);
    }

    /// <summary>
    /// An allowlist that ignores everything and re-includes every directory, <c>!*/</c>, rests the
    /// block's placeholders on that re-include, and it is not named: <c>*</c> decides every path the
    /// block rules on, so it is never a rule that turns one. <c>*</c> itself would ignore the
    /// placeholders the block keeps, and taken out takes nothing from git: it is named as doing nothing
    /// there. One re-including every directory under .harness-config is overruled for runs and
    /// worktrees and needed for every slot, and is not named.
    /// </summary>
    [Fact]
    public async Task AnAllowlistReIncludingEveryDirectory_IsNeverNamed_ForThePlaceholdersRestOnIt()
    {
        var (everything, _) = await FindAsync(before: "*\n!*/\n!.gitignore\n!/.harness-config/config.json\n");
        var ignoresAll = Assert.Single(everything.Conflicts);

        Assert.Equal(("*", 1, false, true), (ignoresAll.Pattern, ignoresAll.Line, ignoresAll.Wins, ignoresAll.Ignores));
        Assert.Equal(
            [
                ".harness-config/sshItems/.gitkeep",
                ".harness-config/wslDistros/.gitkeep",
                ".harness-config/runner/.env/.gitkeep",
                ".harness-config/runner/.secrets/.gitkeep",
            ],
            ignoresAll.Paths);

        var (directories, _) = await FindAsync(before: "/.harness-config/*\n!/.harness-config/*/\n!/.harness-config/config.json\n");

        Assert.Empty(directories.Conflicts);
        Assert.Empty(directories.Unruled);
    }

    /// <summary>
    /// A rule ending in '/' matches no path the check asks about but a directory, as in the tree:
    /// re-including each host's directory before the block does nothing for what they hold, and is
    /// named for that alone - never for a file in the slot, which it cannot match, and which no scratch
    /// repository makes a directory of.
    /// </summary>
    [Fact]
    public async Task AReIncludeOfEveryHostsDirectoryBeforeTheBlock_IsNamedForWhatTheyHoldAlone()
    {
        var (findings, _) = await FindAsync(before: "!/.harness-config/sshItems/*/\n");
        var overruled = Assert.Single(findings.Conflicts);

        Assert.False(overruled.Wins);
        Assert.Equal([".harness-config/sshItems/<any>/<any>"], overruled.Paths);
    }

    /// <summary>
    /// A rule the files the harness keeps in git rest on is never named, though the block overrules it
    /// for every path it rules on: one keeping an action's own files and its placeholder, whatever the
    /// block ignores beside them; one keeping the configuration; and one each of those alone rests on.
    /// Each would be deleted on the strength of the note, and those files with it.
    /// </summary>
    [Theory]
    [InlineData("/.harness-config/**\n!/.harness-config/**/\n!/.harness-config/config.json\n!/.harness-config/runner/actions/**\n", "!/.harness-config/runner/actions/**")]
    [InlineData("*\n!*/\n!/src/**\n!/.harness-config/**\n", "!/.harness-config/**")]
    [InlineData("*\n!*/\n!/.harness-config/*.json\n", "!/.harness-config/*.json")]
    [InlineData("*\n!*/\n!/.harness-config/runner/actions/*/**\n", "!/.harness-config/runner/actions/*/**")]
    public async Task ARuleWhatTheHarnessKeepsRestsOn_IsNeverNamed(string before, string needed)
    {
        var (findings, _) = await FindAsync(before: before);

        Assert.DoesNotContain(findings.Conflicts, conflict => conflict.Pattern == needed);
        Assert.Empty(findings.Unruled);
    }

    /// <summary>
    /// The tree's own ignore files beside its .gitignore files count as much: where .env is excluded
    /// in <c>.git/info/exclude</c>, or in the excludes file the tree's configuration names, the
    /// re-include of the runner's .env slot is what keeps its placeholder, and is not named.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReIncludeTheTreesOtherIgnoreFilesMakeNeeded_IsNeverNamed(bool infoExclude)
    {
        var (findings, _) = await FindAsync(before: "!/.harness-config/runner/.env/\n", excluded: ".env\n", inInfoExclude: infoExclude);

        Assert.Empty(findings.Conflicts);
        Assert.Empty(findings.Unruled);
    }

    /// <summary>
    /// The files the harness keeps in git are its configuration, each placeholder - the actions
    /// directory's too - an action's own files - each one git keeps, and a name standing for any
    /// action's - and each anchor registry git tracks, spelled with forward separators; one git ignores
    /// is kept in no git, and is left out.
    /// </summary>
    [Fact]
    public void TheFilesTheHarnessKeepsInGit_AreItsOwn_AndTheRegistriesGitTracks()
    {
        var kept = InitService.KeptInGit(
            [
                new AnchorRegistry(AnchorRegistryKind.Pending, "docs\\anchors\\pending.md", "C:/tree/docs/anchors/pending.md", IsIgnored: false),
                new AnchorRegistry(AnchorRegistryKind.Done, "docs/anchors/done.md", "C:/tree/docs/anchors/done.md", IsIgnored: true),
            ],
            [".harness-config/runner/actions/probe/bin/helper.exe"]);

        Assert.Contains(".harness-config/config.json", kept);
        Assert.Contains(".harness-config/runner/actions/.gitkeep", kept);
        Assert.Contains(".harness-config/sshItems/.gitkeep", kept);
        Assert.Contains($".harness-config/runner/actions/{ManagedIgnoreRule.AnyDirectoryName}/{ManagedIgnoreRule.AnyName}", kept);
        Assert.Contains(".harness-config/runner/actions/probe/bin/helper.exe", kept);
        Assert.Contains("docs/anchors/pending.md", kept);
        Assert.DoesNotContain("docs/anchors/done.md", kept);
        Assert.All(kept, path => Assert.DoesNotContain('\\', path));
    }

    /// <summary>
    /// A re-include of a directory nothing excludes changes nothing, and is named as doing nothing
    /// there: the slot's own, and all of .harness-config, which reads as keeping it in git while the
    /// block keeps the host keys and the run records out.
    /// </summary>
    [Fact]
    public async Task AReIncludeNothingNeeds_IsNamedAsDoingNothing()
    {
        var (slot, _) = await FindAsync(before: "!/.harness-config/sshItems/\n");
        var overruled = Assert.Single(slot.Conflicts);

        Assert.Equal(("!/.harness-config/sshItems/", 1, false, false), (overruled.Pattern, overruled.Line, overruled.Wins, overruled.Ignores));
        Assert.Equal([".harness-config/sshItems/<any>", ".harness-config/sshItems/<any>/<any>"], overruled.Paths);

        var (everything, _) = await FindAsync(before: "!/.harness-config/\n");
        var all = Assert.Single(everything.Conflicts);

        Assert.False(all.Wins);
        Assert.Contains(".harness-config/lock.json", all.Paths);
        Assert.Contains(".harness-config/sshItems/<any>", all.Paths);
    }

    /// <summary>
    /// A re-include of a slot that a nested ignore file excludes again is overruled there - by a rule
    /// in another file, though on the same line of it - and taken out changes nothing: it is named as
    /// doing nothing, beside the nested rule, which takes the placeholder.
    /// </summary>
    [Fact]
    public async Task AReIncludeOfASlot_ThatANestedFileOverrules_IsNamedAsDoingNothing()
    {
        var (findings, _) = await FindAsync(before: Allowlist, nested: "# the slot, again\n/sshItems/\n");

        var nested = Assert.Single(findings.Conflicts, conflict => conflict.Wins);
        var overruled = Assert.Single(findings.Conflicts, conflict => !conflict.Wins);

        Assert.Equal(".harness-config/.gitignore", nested.Source);
        Assert.Equal([".harness-config/sshItems/.gitkeep"], nested.Paths);
        Assert.Equal((".gitignore", 2, "!/.harness-config/sshItems/"), (overruled.Source, overruled.Line, overruled.Pattern));
        Assert.Equal([".harness-config/sshItems/<any>", ".harness-config/sshItems/<any>/<any>"], overruled.Paths);
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
            InitService.KeptInGit([], []),
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
            InitService.KeptInGit([], []),
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
            InitService.KeptInGit([], []),
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
                InitService.KeptInGit([], []),
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
            + "'.harness-config/runner/.env/<any>/<any>', which the managed block ignores; another rule decides them, so this "
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

    /// <summary>
    /// init names none of an allowlist's re-includes of the slots the block keeps a placeholder in, and
    /// git keeps each placeholder in reach of git add through them, under the runner too.
    /// </summary>
    [Fact]
    public async Task Init_NamesNoneOfAnAllowlistsSlotReIncludes_WhichItsPlaceholdersRestOn()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", Allowlist);

        var outcome = await harness.InitService.InitializeAsync(temp.Path, token);
        var placeholders = await harness.GitClient.ExplainIgnoredAsync(
            temp.Path,
            [".harness-config/sshItems/.gitkeep", ".harness-config/runner/.env/.gitkeep"],
            token);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.DoesNotContain(outcome.Details!, line => line.StartsWith("note", StringComparison.Ordinal));
        Assert.All(placeholders, placeholder => Assert.False(placeholder.Ignored));
    }

    /// <summary>
    /// init names no rule an action's own file rests on: a re-include of the actions directory after a
    /// rule ignoring what an action holds - a bin directory, as Visual Studio's template ignores, a kind of
    /// file, or a directory an action is grouped in - keeps that file in git, and taking it out would take
    /// the file with it. With no such file in the tree, the same rule is named for what the block ignores
    /// beside the actions, since taking it out then takes nothing from git. A file git already tracks
    /// counts as one it would add does.
    /// </summary>
    [Theory]
    [InlineData("[Bb]in/", "probe/bin/helper.exe", false)]
    [InlineData("[Bb]in/", "probe/bin/helper.exe", true)]
    [InlineData("*.sh", "deploy/scripts/run.sh", false)]
    [InlineData("sub/", "group/sub/action.yml", false)]
    public async Task Init_NamesNoRuleAnActionsOwnFileRestsOn(string hiding, string file, bool tracked)
    {
        const string ReInclude = "!/.harness-config/runner/actions/**";

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", $"{hiding}\n{ReInclude}\n");

        var alone = await harness.InitService.InitializeAsync(temp.Path, token);

        Assert.Contains(
            alone.Details!,
            line => line.Contains($"('{ReInclude}')", StringComparison.Ordinal) && line.EndsWith("so this one does nothing there", StringComparison.Ordinal));

        temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", file), "kept\n");

        if (tracked)
        {
            await harness.CommitAllAsync(temp.Path, "the action's file", token);
        }

        var outcome = await harness.InitService.InitializeAsync(temp.Path, token);
        var kept = await harness.GitClient.ExplainIgnoredAsync(temp.Path, [$".harness-config/runner/actions/{file}"], token);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.DoesNotContain(outcome.Details!, line => line.Contains($"('{ReInclude}')", StringComparison.Ordinal));
        Assert.False(Assert.Single(kept).Ignored);
    }

    /// <summary>
    /// An excludes file the tree's configuration sets to nothing is none, as git reads it - never git's
    /// default one, which a tree leaving the name unset reads: with .env excluded there, the re-include of
    /// the runner's .env slot keeps the placeholder in a tree that reads it, and is not named, and rests on
    /// nothing in one that reads none, and is named as doing nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExcludesFileSetToNothing_IsNone_ThoughGitsDefaultOneExcludes(bool setToNothing)
    {
        using var temp = new TempDirectory();
        using var home = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", "!/.harness-config/runner/.env/\n");
        await harness.InitService.InitializeAsync(temp.Path, token);

        if (setToNothing)
        {
            Assert.True((await harness.GitClient.RunAsync(temp.Path, ["config", "core.excludesFile", string.Empty], cancellationToken: token)).Succeeded);
        }

        // git's default excludes file, and a configuration naming none, for the git this check runs alone.
        home.WriteFile(Path.Combine("git", "ignore"), ".env\n");

        var git = new GitClient(
            new ProcessesWith(harness.ProcessRunner, new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["XDG_CONFIG_HOME"] = home.Path,
                ["GIT_CONFIG_GLOBAL"] = home.WriteFile("gitconfig", string.Empty),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            }),
            harness.Output);
        var findings = await new ManagedIgnoreCheck(git, harness.FileSystem, harness.Platform, harness.Output).FindAsync(
            temp.Path,
            File.ReadAllText(temp.Combine(".gitignore")),
            InitService.BuildIgnoreRules(new WorktreeSettings()),
            InitService.KeptInGit([], []),
            token);

        if (setToNothing)
        {
            var named = Assert.Single(findings.Conflicts);

            Assert.Equal("!/.harness-config/runner/.env/", named.Pattern);
            Assert.False(named.Wins);
        }
        else
        {
            Assert.Empty(findings.Conflicts);
        }

        Assert.Empty(findings.Unruled);
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

    /// <summary>
    /// An allowlist as a repository writes one: everything under .harness-config excluded, and each
    /// directory it keeps in git re-included - the slots the block keeps a placeholder in among them -
    /// with the runner's own contents excluded again, so its slots rest on their re-includes too.
    /// </summary>
    private const string Allowlist = "/.harness-config/*\n!/.harness-config/sshItems/\n!/.harness-config/wslDistros/\n"
        + "!/.harness-config/runner/\n/.harness-config/runner/*\n!/.harness-config/runner/actions/\n"
        + "!/.harness-config/runner/.env/\n!/.harness-config/runner/.secrets/\n";

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
    /// .gitignore when given, and <paramref name="excluded"/> in its <c>.git/info/exclude</c> - or, where
    /// <paramref name="inInfoExclude"/> is false, in an excludes file its configuration names - and asks
    /// which rules turn a managed path.
    /// </summary>
    private static async Task<(ManagedIgnoreFindings Findings, string Content)> FindAsync(
        string before = "",
        string after = "",
        string? nested = null,
        string? excluded = null,
        bool inInfoExclude = true)
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);

        if (excluded is not null && inInfoExclude)
        {
            Directory.CreateDirectory(temp.Combine(".git", "info"));
            File.WriteAllText(temp.Combine(".git", "info", "exclude"), excluded);
        }
        else if (excluded is not null)
        {
            var excludesFile = elsewhere.WriteFile("excludes", excluded);

            Assert.True((await harness.GitClient.RunAsync(temp.Path, ["config", "core.excludesFile", excludesFile], cancellationToken: token)).Succeeded);
        }

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
        var findings = await Check(harness).FindAsync(temp.Path, content, InitService.BuildIgnoreRules(new WorktreeSettings()), InitService.KeptInGit([], []), token);

        return (findings, content);
    }

    /// <summary>Runs every program through <paramref name="inner"/>, with <paramref name="environment"/> set over its own.</summary>
    private sealed class ProcessesWith(IProcessRunner inner, IReadOnlyDictionary<string, string?> environment) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var merged = new Dictionary<string, string?>(request.Environment, StringComparer.Ordinal);

            foreach (var (name, value) in environment)
            {
                merged[name] = value;
            }

            return inner.RunAsync(request with { Environment = merged }, cancellationToken);
        }

        public string? FindExecutable(string command) => inner.FindExecutable(command);
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
