using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// The defects a review of this branch found, each pinned by the behaviour that was wrong. Every one
/// of them was a guard reporting something it had not established, or a rule claiming ownership of
/// text it did not own — so each test here fails if the fix is reverted, rather than merely covering
/// the line.
/// </summary>
public sealed class ReviewFixesTests
{
    private const string Tree = "/tree";
    private const string Build = "/tree/build/x86_64-gcc-debug";

    private static LegPaths Paths => new(Tree, Build);

    /// <summary>
    /// A step's run line is a program's own text. Refusing every brace group in it turned away
    /// working action files for using the shell, which is the ordinary way to write one.
    /// </summary>
    [Theory]
    [InlineData("${HOME}/out")]
    [InlineData("${PWD}/opt")]
    [InlineData("awk '{print}'")]
    [InlineData("--prefix=${VERSION}")]
    public void ABraceGroupThisToolDoesNotOwn_SurvivesAnActionRunLine(string written)
    {
        Assert.Equal(written, LegPathNames.Expand(written, Paths, "step", PlaceholderPolicy.LeaveAsWritten));

        // And the load-time check over the whole file agrees with what expansion does, which is the
        // only way the two can be read as one rule.
        LegPathNames.RefuseUnknown(written, "step", PlaceholderPolicy.LeaveAsWritten);
    }

    /// <summary>
    /// The typo that is still worth refusing, under either policy: no other vocabulary spells a name
    /// that differs from one of these only in case, and left alone it reaches the runner as literal
    /// text.
    /// </summary>
    [Theory]
    [InlineData("{builddir}")]
    [InlineData("{BuildDir}")]
    [InlineData("{TREEDIR}")]
    public void AKnownNameSpelledDifferently_IsRefusedEvenWhereBracesAreLeftAlone(string written)
    {
        var refusal = Assert.Throws<HarnessException>(
            () => LegPathNames.Expand(written, Paths, "step", PlaceholderPolicy.LeaveAsWritten));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("spelled differently", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A setting this tool owns keeps the refusal: there is no shell in <c>test.args</c>, so a name
    /// nothing fills in is a typo, and found at load it names the line instead of costing the build
    /// that would have preceded it.
    /// </summary>
    [Fact]
    public void AnUnknownNameInASettingThisToolOwns_IsStillRefused()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => LegPathNames.RefuseUnknown("{buildDirectory}", "test.args"));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
    }

    /// <summary>
    /// The core count is spliced into <c>coresArgs</c> and nowhere else, so the same name in
    /// <c>args</c> reaches the runner as text. The check and the expansion disagreed about it: one
    /// waved it through at load and the other killed the leg at run time.
    /// </summary>
    [Fact]
    public void TheCoreCountsName_IsLegalWhereItIsSpliced_AndRefusedWhereItIsNot()
    {
        LegPathNames.RefuseUnknown(
            CoreCounts.Placeholder,
            "test.coresArgs",
            PlaceholderPolicy.Refuse,
            [CoreCounts.PlaceholderName]);

        Assert.Throws<HarnessException>(
            () => LegPathNames.RefuseUnknown(CoreCounts.Placeholder, "test.args"));
    }

    /// <summary>
    /// Substituting the tree root for a build directory nobody has is the exact failure this
    /// vocabulary was written to end: ctest started at the tree root of an out-of-source project
    /// reports no tests and exits in under a fifth of a second.
    /// </summary>
    [Fact]
    public void TheBuildDirectorysName_WithNoLegToSupplyIt_IsRefusedRatherThanSubstituted()
    {
        var refusal = Assert.Throws<HarnessException>(() => LegPathNames.Expand(
            "--test-dir {buildDir}",
            new LegPaths(Tree, buildDirectory: null),
            "step",
            PlaceholderPolicy.LeaveAsWritten));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("reaches no leg", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Each of the three names this vocabulary owns resolves to its own directory.</summary>
    [Fact]
    public void EveryNameThisVocabularyOwns_ResolvesToItsOwnDirectory()
    {
        Assert.Equal(Build, LegPathNames.Expand("{buildDir}", Paths, "test.args"));
        Assert.Equal(Tree, LegPathNames.Expand("{treeDir}", Paths, "test.args"));
        Assert.Equal(Paths.HarnessDirectory, LegPathNames.Expand("{harnessDir}", Paths, "test.args"));

        // Not the same directory as the tree, which is what a reader would never notice going wrong.
        Assert.NotEqual(Tree, Paths.HarnessDirectory);
    }

    /// <summary>
    /// A set that is empty because nothing matched and a set nobody could establish are opposite
    /// facts, and written as a list beside a reason they were one forgotten field apart. One member
    /// with three named states cannot be built into the fourth.
    /// </summary>
    [Fact]
    public void AnEmptySetOfInputs_IsTheSameThingAsNoneAtAll()
    {
        Assert.Same(LegInputs.None, LegInputs.Watch([]));
        Assert.False(LegInputs.None.Watching);
        Assert.Null(LegInputs.None.Unmeasurable);

        Assert.True(LegInputs.Watch(["src/a.cpp"]).Watching);

        var unmeasured = LegInputs.Unmeasured("git could not be asked");
        Assert.False(unmeasured.Watching);
        Assert.Equal("git could not be asked", unmeasured.Unmeasurable);
    }

    /// <summary>
    /// A far side that answers the prune without the member at all deserialises it as null, and the
    /// null is dereferenced while composing the warning that names what kept the directory. The
    /// sibling answer on this protocol defaults its list for exactly this reason.
    /// </summary>
    [Fact]
    public void APruneAnswerWithNoHeldMember_ReadsAsAnEmptyList_NotNull()
    {
        var answer = JsonSerializer.Deserialize<EmptiedDirectory>(
            """{"path":"docs","removed":false}""",
            HostAgentProtocol.JsonOptions);

        Assert.NotNull(answer);
        Assert.NotNull(answer.Held);
        Assert.Empty(answer.Held);

        // And the sentence that reads it does not throw.
        Assert.Equal(string.Empty, string.Join(", ", answer.Held));
    }

    /// <summary>
    /// The two states that mean something are the only two that can be written. Removed-with-names
    /// and kept-with-nothing are both readable as a sentence and neither is true of anything.
    /// </summary>
    [Fact]
    public void ADirectoryAnsweredFor_IsEitherGoneOrKeptBySomething()
    {
        var gone = EmptiedDirectory.Gone("docs/old");
        Assert.True(gone.Removed);
        Assert.Empty(gone.Held);

        var kept = EmptiedDirectory.Kept("docs", ["cache.pyc"]);
        Assert.False(kept.Removed);
        Assert.Equal(["cache.pyc"], kept.Held);
    }

    /// <summary>
    /// The prune walks up from the directory it emptied, and a parent that still holds something is
    /// where the tree keeps its other files. Reported as a copy that diverges, it put a warning with
    /// every clause false on almost every sync that deletes a nested directory.
    /// </summary>
    [Fact]
    public async Task ANonEmptyParentOfAnEmptiedDirectory_IsNotReportedAsADivergence()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        // What the tree keeps, beside a directory the deletion has just emptied.
        Directory.CreateDirectory(temp.Combine("src", "retired"));
        await File.WriteAllTextAsync(temp.Combine("src", "keep.txt"), "kept", token);

        var answered = await Transport(harness)
            .RemoveEmptyDirectoriesAsync(temp.Path, ["src/retired"], token);

        var gone = Assert.Single(answered);
        Assert.Equal("src/retired", gone.Path);
        Assert.True(gone.Removed);

        // 'src' is not answered for at all: it holds keep.txt, which this sync manages, and it was
        // never emptied by anything.
        Assert.DoesNotContain(answered, entry => entry.Path == "src");
        Assert.True(Directory.Exists(temp.Combine("src")));
    }

    /// <summary>
    /// The directory the plan emptied, kept alive by content the configuration protects, is still
    /// answered for — that is the case the check exists for, and narrowing the report must not have
    /// cost it.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheDeletionEmptied_ThatSomethingElseKeeps_IsStillNamed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        Directory.CreateDirectory(temp.Combine("pkg"));
        await File.WriteAllTextAsync(temp.Combine("pkg", "cache.pyc"), "bytecode", token);

        var answered = await Transport(harness)
            .RemoveEmptyDirectoriesAsync(temp.Path, ["pkg"], token);

        var kept = Assert.Single(answered);
        Assert.Equal("pkg", kept.Path);
        Assert.False(kept.Removed);
        Assert.Contains("cache.pyc", kept.Held);
    }

    /// <summary>
    /// <c>lineEndings.exclude</c> is read by the matcher <c>sync.exclude</c> is read by, so an entry
    /// written the same way covers the same thing. Compared as literal text, as it was, it covered
    /// nothing and said nothing.
    /// </summary>
    [Fact]
    public void AnyDepthInLineEndingsExclude_CoversWhatItDoesInSyncExclude()
    {
        Assert.True(PathPatterns.Matches(["**/node_modules"], "web/app/node_modules/x.js"));

        // Whole segments, here as everywhere: a directory with a similar name is a different one.
        Assert.False(PathPatterns.Matches(["**/node_modules"], "web/my_node_modules/x.js"));
    }

    /// <summary>
    /// And the setting is checked by the rule its sibling is checked by, so a pattern that would be
    /// compared as text is named at load instead of sitting in the file reading as protection.
    /// </summary>
    [Fact]
    public void APatternLineEndingsCannotHonour_IsNamedWhenTheConfigurationIsRead()
    {
        var problems = HarnessConfigValidator.Validate(new HarnessConfig
        {
            LineEndings = new LineEndingSettings { Exclude = ["src/**/cache"] },
        });

        Assert.Contains(
            problems,
            problem => problem.Contains("lineEndings.exclude", StringComparison.Ordinal)
                && problem.Contains("src/**/cache", StringComparison.Ordinal));
    }

    private static LocalSyncTransport Transport(HarnessFactory harness)
        => new(
            harness.FileSystem,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            harness.GitClient,
            harness.Platform);
}
