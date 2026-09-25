using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A build passes only on evidence that it produced something. Each test here answers a way a build
/// was measured reporting success having produced nothing, after which the tests run against
/// whatever the previous build left behind.
/// </summary>
public sealed class BuildServiceTests
{
    private const string Leg = "win-msvc-release";

    [Fact]
    public async Task AProjectDeclaringNoBuildOutputs_IsUnwitnessed_RatherThanPassedOnItsExitCode()
    {
        // An empty list makes the witness vacuously true rather than absent, which reads as a check
        // that passed when it is a check nobody performed.
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken)).BuildAsync(
            Config(),
            Request(temp, outputs: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(result.Verdict.Verdict));
        Assert.Contains("declares no buildOutputs", result.Verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// worktrees.pathBudgetReserve is a number measured once, against whatever the build produced
    /// then. Every build measures what it actually left below its build directory and says so, with
    /// both numbers, when that went deeper - and says nothing when it did not, at the boundary too.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public async Task ABuildDeeperThanTheReserve_SaysSo_WithBothNumbers(int reserveAgainstDeepest, bool warned)
    {
        using var temp = new TempDirectory();
        var request = Request(temp, outputs: ["bin/app.dll"]);
        var deepest = Path.Combine("obj", "nested", "deeper", "still", "file.obj");
        var (service, factory) = await TrackedWithFactoryAsync(temp, TestContext.Current.CancellationToken, leaves: [Path.Combine("bin", "app.dll"), deepest]);
        var reserve = deepest.Length + reserveAgainstDeepest;

        await service.BuildAsync(
            new HarnessConfig
            {
                Defaults = new HarnessDefaults { StallSeconds = 0 },
                Worktrees = new WorktreeSettings { PathBudgetReserve = reserve },
            },
            request,
            TestContext.Current.CancellationToken);

        var said = factory.StandardError.ToString();

        Assert.Equal(warned, said.Contains("worktrees.pathBudgetReserve declares", StringComparison.Ordinal));

        if (warned)
        {
            Assert.Contains($"{deepest.Length} characters long", said, StringComparison.Ordinal);
            Assert.Contains($"declares {reserve}", said, StringComparison.Ordinal);

            // This build's own, not a path it merely found: the two read alike but call for different
            // remedies, and only one of them is the reserve's to answer for.
            Assert.Contains("This build wrote it", said, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A build records what its directory came to, from the walk it already makes of it, so that the room
    /// its next build from clean needs - and a first build of its variant in another copy - is known without
    /// walking the directory again.
    /// </summary>
    [Fact]
    public async Task ABuild_RecordsWhatItsDirectoryCameTo()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var request = Request(temp, outputs: ["bin/app.dll"]);
        string[] leaves = [Path.Combine("bin", "app.dll"), Path.Combine("obj", "a.o"), Path.Combine("obj", "b.o")];
        var (service, _) = await TrackedWithFactoryAsync(temp, token, leaves: leaves);

        Assert.Equal(LegVerdict.Passed, (await service.BuildAsync(Config(), request, token)).Verdict.Verdict);

        var recorded = BuildRecord.Parse(await File.ReadAllTextAsync(RecordOf(request, temp), token)).Bytes;
        var directory = request.Variant.DirectoryUnder(temp.Path);

        // Every file the build left, and the record as it was begun; never more than the directory now holds,
        // whose record has since grown by what it records.
        Assert.NotNull(recorded);
        Assert.InRange(recorded.Value, leaves.Length * "built".Length, new PhysicalFileSystem(FilePermissionsFactory.Create()).DirectorySize(directory));
    }

    /// <summary>
    /// A path an earlier build left is reported as a leftover, with the remedy for one, and never as
    /// something this build produced.
    /// </summary>
    /// <remarks>
    /// A directory kept between builds holds the objects of targets since renamed or removed, and the
    /// deepest path in it is often one of those. A consumer measured it: a test renamed shorter left its
    /// old object behind, four legs warned that "this build produced" it and told them to raise the
    /// reserve - which their own budget arithmetic had no room for - while the two legs that had rebuilt
    /// from clean, and so held only what they wrote, said nothing.
    /// </remarks>
    [Fact]
    public async Task APathAnEarlierBuildLeft_IsSaidAsALeftover_WithTheRemedyForOne()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var request = Request(temp, outputs: ["bin/app.dll"]);
        var (service, factory) = await TrackedWithFactoryAsync(temp, token, leaves: [Path.Combine("bin", "app.dll")]);

        var config = new HarnessConfig
        {
            Defaults = new HarnessDefaults { StallSeconds = 0 },
            Worktrees = new WorktreeSettings { PathBudgetReserve = 40 },
        };

        // A first build, so the directory is kept rather than started from clean for having no record.
        Assert.Equal(LegVerdict.Passed, (await service.BuildAsync(config, request, token)).Verdict.Verdict);

        // What an earlier build left: deeper than the reserve, and dated before this build begins.
        var leftover = Path.Combine(
            request.Variant.DirectoryUnder(temp.Path),
            "obj",
            "a-target-that-no-longer-exists",
            "with-a-very-long-name-indeed-left-behind.cpp.obj");

        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "stale\n", token);
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-6));

        factory.StandardError.GetStringBuilder().Clear();

        Assert.Equal(LegVerdict.Passed, (await service.BuildAsync(config, request, token)).Verdict.Verdict);

        var said = factory.StandardError.ToString();

        // Said as a path this build did not write, with both readings a date can support and the remedy for
        // each - and never as something this build produced.
        Assert.Contains("the deepest path below this build directory is", said, StringComparison.Ordinal);
        Assert.Contains("with-a-very-long-name-indeed-left-behind.cpp.obj", said, StringComparison.Ordinal);
        Assert.Contains("This build did not write it", said, StringComparison.Ordinal);
        Assert.Contains("one that no longer exists", said, StringComparison.Ordinal);
        Assert.Contains("start this variant's build directory from clean", said, StringComparison.Ordinal);
        Assert.DoesNotContain("this build produced a path", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where ninja says a leftover is an output no target of this build produces any more - the object of a
    /// target renamed away - it is left out of the reserve's check, which a new worktree's build, starting from
    /// clean, never holds it for; noted rather than warned about, naming what removes it, and only where it is
    /// deeper than the reserve. A consumer's incremental builds warned about one such object every time.
    /// </summary>
    [Fact]
    public async Task ALeftoverNinjaSaysIsDead_IsLeftOutOfTheCheck_AndNoted_NamingWhatRemovesIt()
    {
        var (said, noted) = await LeftoverAsync("Cleaning...\nRemove obj/a-target-that-no-longer-exists/with-a-very-long-name-indeed-left-behind.cpp.obj\n1 files.\n");

        Assert.DoesNotContain("worktrees.pathBudgetReserve declares", said, StringComparison.Ordinal);
        Assert.Contains("1 output(s) below this build directory are ones no target of this build produces any more", noted, StringComparison.Ordinal);
        Assert.Contains("with-a-very-long-name-indeed-left-behind.cpp.obj", noted, StringComparison.Ordinal);
        Assert.Contains("'ninja -t cleandead' in this build directory removes them", noted, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leftover ninja did not call dead is still measured against the reserve - a target this build had no
    /// reason to rebuild wrote it, or something that is no target's output did - and said so, never as one that
    /// no longer exists; a ninja that could not say keeps the wording that says either.
    /// </summary>
    [Fact]
    public async Task ALeftoverNinjaDidNotCallDead_IsStillMeasured_AndSaidAsOneATargetOrSomethingElseWrote()
    {
        var (said, noted) = await LeftoverAsync("Cleaning...\n0 files.\n");

        Assert.Contains("worktrees.pathBudgetReserve declares", said, StringComparison.Ordinal);
        Assert.Contains("ninja counts it among no target's dead outputs", said, StringComparison.Ordinal);
        Assert.DoesNotContain("one that no longer exists", said, StringComparison.Ordinal);
        Assert.DoesNotContain("produces any more", noted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds once, leaves an old object deeper than the reserve in a ninja build directory, builds again with
    /// ninja answering <paramref name="ninja"/>, and returns what the second build warned and noted.
    /// </summary>
    private static async Task<(string Warned, string Noted)> LeftoverAsync(string ninja)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var request = Request(temp, outputs: ["bin/app.dll"]);
        var (service, factory) = await TrackedWithFactoryAsync(temp, token, leaves: [Path.Combine("bin", "app.dll")], deadOutputs: new RecordingRunner(ninja));

        var config = new HarnessConfig
        {
            Defaults = new HarnessDefaults { StallSeconds = 0 },
            Worktrees = new WorktreeSettings { PathBudgetReserve = 40 },
        };

        Assert.Equal(LegVerdict.Passed, (await service.BuildAsync(config, request, token)).Verdict.Verdict);

        var directory = request.Variant.DirectoryUnder(temp.Path);
        var leftover = Path.Combine(directory, "obj", "a-target-that-no-longer-exists", "with-a-very-long-name-indeed-left-behind.cpp.obj");

        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "stale\n", token);
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-6));
        await File.WriteAllTextAsync(Path.Combine(directory, NinjaDependencyCheck.ManifestFileName), string.Empty, token);

        factory.StandardError.GetStringBuilder().Clear();
        factory.StandardOutput.GetStringBuilder().Clear();

        Assert.Equal(LegVerdict.Passed, (await service.BuildAsync(config, request, token)).Verdict.Verdict);

        return (factory.StandardError.ToString(), factory.StandardOutput.ToString());
    }

    [Fact]
    public async Task ABuildThatExitedZeroAndProducedNothingItDeclared_IsUnwitnessed()
    {
        // The result that hands the tests a stale binary: the build system had nothing to do, said
        // so with a zero exit code, and the objects on disk are the previous build's.
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken, leaves: [])).BuildAsync(
            Config(),
            Request(temp, outputs: ["bin/app.dll"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains("bin/app.dll", result.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildThatProducedWhatItDeclared_Passes()
    {
        using var temp = new TempDirectory();
        var request = Request(temp, outputs: ["bin/app.dll"]);

        // Left where the build puts it, so the witness has something to find.
        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken, leaves: ["bin/app.dll"])).BuildAsync(
            Config(),
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task ABuildThatFailed_KeepsItsOwnVerdict_AndIsNeverAskedForOutputs()
    {
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken, exitCode: 2)).BuildAsync(
            Config(),
            Request(temp, outputs: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);
    }

    /// <summary>
    /// The measurement that opened this: from a consumer's worktree, one markdown edit made two
    /// legs rebuild from clean, discarding a warm build directory that had cost eleven minutes.
    /// Neither named file is read by the build - so even an edit dated before the build, which would
    /// rebuild from clean were it an input, keeps the directory.
    /// </summary>
    [Fact]
    public async Task ADocumentationEdit_KeepsTheWarmBuildDirectory()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await EditAsync(temp, "docs/guide.md", "rewritten\n", HoursFromNow(-2), token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.Null(again.RebuiltFromClean);
    }

    /// <summary>
    /// A tree git tracks nothing in - as a copy a sync made was, its files written and none staged - is
    /// said as that: nothing is watched while it builds, and the record it leaves holds no fingerprint,
    /// so the next build starts from clean and says why. Measured on a consumer's host copy: every build
    /// after the first rebuilt from clean, naming no cause but the empty record.
    /// </summary>
    [Fact]
    public async Task ATreeGitTracksNothingIn_IsSaidAsThat_AndItsNextBuildStartsFromClean()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        var emptied = await factory.GitClient.RunAsync(temp.Path, ["read-tree", "--empty"], cancellationToken: token);
        Assert.True(emptied.Succeeded, emptied.FailureMessage);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);
        Assert.Contains($"git tracks no file in '{temp.Path}'", factory.StandardError.ToString(), StringComparison.Ordinal);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith(
            "no record to compare: the previous build recorded no input fingerprint - git tracked none of its inputs",
            again.RebuiltFromClean,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The same tree with its index put right, as each sync now puts a copy's: its build fingerprints its
    /// inputs, and the next build keeps the warm directory rather than starting from clean.
    /// </summary>
    [Fact]
    public async Task ATreeWhoseIndexWasPutRight_KeepsItsWarmBuildDirectory()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        IReadOnlyList<string> files = [.. (await factory.GitClient.ListIndexAsync(temp.Path, token)).Select(entry => entry.Path)];

        var emptied = await factory.GitClient.RunAsync(temp.Path, ["read-tree", "--empty"], cancellationToken: token);
        Assert.True(emptied.Succeeded, emptied.FailureMessage);

        await factory.GitClient.IndexExactlyAsync(temp.Path, files, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.Null(again.RebuiltFromClean);
        Assert.DoesNotContain("git tracks no file", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An edit to something the build reads, dated after the build as an edit is, is the build
    /// system's to act on: newer than every output the build left, it rebuilds whatever reads it, and
    /// the warm directory is kept. Rebuilt from clean instead, one edit to one input cost a consumer
    /// 1,186 steps from nothing, and ran past its caller's time limit.
    /// </summary>
    [Theory]
    [InlineData("src/app.cpp")]
    [InlineData("VERSION")]
    public async Task AnEditDatedAfterTheBuild_KeepsTheWarmBuildDirectory_ForTheBuildSystemToAct(string edited)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await EditAsync(temp, edited, "changed\n", HoursFromNow(1), token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.Null(again.RebuiltFromClean);
    }

    /// <summary>
    /// The half that must never be lost: a file whose content differs from what this directory was
    /// built from, dated no later than that build - as a stepped clock or a tool keeping a file's old
    /// time leaves it - is one a build system comparing times could miss, and the directory is
    /// rebuilt from clean, saying which file and which condition.
    /// </summary>
    [Theory]
    [InlineData("src/app.cpp")]
    [InlineData("VERSION")]
    public async Task AnEditDatedNoLaterThanTheBuild_DiscardsIt_AndSaysWhichConditionFired(string edited)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await EditAsync(temp, edited, "changed\n", HoursFromNow(-2), token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.Contains(edited, again.RebuiltFromClean, StringComparison.Ordinal);

        // Which condition fired, not only which file differed. A consumer measured their file against
        // the timestamp rule, found it did not hold, and could not tell from the line whether the
        // clock-step condition had fired instead.
        Assert.StartsWith("a change a build system could miss:", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A change is dated against the newest file the build left, not its record or its binary: a host
    /// whose clock steps forward for a moment and back stamps an object compiled in that moment ahead
    /// of everything written after it, and a phase that starts and ends outside the step measures no
    /// drift. An edit dated after the record, but not after that object, is one the build system could
    /// miss - and the line names the object and both dates, so whoever reads it can see what the
    /// change was held to.
    /// </summary>
    [Fact]
    public async Task AChange_IsDatedAgainstTheNewestFileTheBuildLeft_WhichTheLineNames()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var ahead = HoursFromNow(3);
        var edited = HoursFromNow(1);
        var stepped = Service(factory, exitCode: 0, phases: new ScriptedPhases(building: () => WriteBuilt(request, temp, "obj/app.o", ahead)));

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token, stepped)).Verdict.Verdict);

        await EditAsync(temp, "src/app.cpp", "changed\n", edited, token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("a change a build system could miss: 'src/app.cpp'", again.RebuiltFromClean, StringComparison.Ordinal);
        Assert.Contains($"dated {edited:u}, no later than 'obj/app.o' at {ahead:u}, the newest file that build left", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// What is written into the directory after the build ends is not what the build left: ctest writes
    /// its logs there as a suite ends, and an edit made while the suite ran is dated before them. Dated
    /// against the directory as it stands, that edit rebuilt everything from clean; dated against what
    /// the build left, it is the build system's to act on.
    /// </summary>
    [Fact]
    public async Task AnEditMadeWhileTheTestsRan_IsNotDatedAgainstWhatTheyWroteAfterTheBuild()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await EditAsync(temp, "src/app.cpp", "changed\n", HoursFromNow(1), token);
        WriteBuilt(request, temp, "Testing/Temporary/LastTest.log", HoursFromNow(2));

        Assert.Null((await BuildOnceAsync(factory, request, token)).RebuiltFromClean);
    }

    /// <summary>
    /// A change dated to the same instant as the newest file in the directory is one a build system
    /// could miss: ninja and make rebuild what is older than an input, never what is as old, and a
    /// file system that keeps whole seconds dates an edit made in the second the build ended exactly
    /// so.
    /// </summary>
    [Fact]
    public async Task AChangeDatedTheSameInstantAsTheNewestFile_IsOneABuildSystemCouldMiss()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        var instant = HoursFromNow(1);
        var compiling = Service(factory, exitCode: 0, phases: new ScriptedPhases(building: () => WriteBuilt(request, temp, "obj/app.o", instant)));

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token, compiling)).Verdict.Verdict);

        await EditAsync(temp, "src/app.cpp", "changed\n", instant, token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("a change a build system could miss: 'src/app.cpp'", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// An input the record never held - new to the set since, as a file first committed after the
    /// build that compiled it is - is judged by the build system alone, as every file outside the set
    /// is, however it is dated: no recorded content says it changed.
    /// </summary>
    [Fact]
    public async Task AnInputNewToTheSetSinceTheBuild_IsJudgedByTheBuildSystemAlone()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await EditAsync(temp, "src/extra.cpp", "int extra;\n", HoursFromNow(-2), token);
        await factory.RunGitAsync(temp.Path, ["add", "src/extra.cpp"], token);

        Assert.Null((await BuildOnceAsync(factory, request, token)).RebuiltFromClean);
    }

    /// <summary>
    /// A build that fails still leaves what it compiled, from the tree it was given, and the build
    /// after it dates its changes from that tree: a fix dated after what the failed build compiled
    /// keeps the directory, and one dated before it, which a build system would miss, does not.
    /// Dated from the last build that passed instead, every change made before the failure is older
    /// than what the failed build compiled, and fixing a compile error would rebuild everything from
    /// clean.
    /// </summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(1.5, true)]
    public async Task AFailedBuild_LeavesTheTreeItWasGiven_ForTheFixToBeDatedAgainst(double fixedAt, bool rebuilds)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        // Two edits, and a build that compiles one of them and fails on the other.
        await EditAsync(temp, "VERSION", "2.0.0\n", HoursFromNow(1), token);
        await EditAsync(temp, "src/app.cpp", "int main({}\n", HoursFromNow(1), token);

        var failing = Service(
            factory,
            exitCode: 0,
            phases: new ScriptedPhases(building: () => WriteBuilt(request, temp, "obj/version.o", HoursFromNow(2)), buildExitCode: 1));
        var failed = await failing.BuildAsync(Config(), request, token);

        Assert.Equal(LegVerdict.Failed, failed.Verdict.Verdict);

        await EditAsync(temp, "src/app.cpp", "int main(){}\n", HoursFromNow(fixedAt), token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.Equal(rebuilds, again.RebuiltFromClean is not null);
        Assert.True(!rebuilds || again.RebuiltFromClean!.StartsWith("a change a build system could miss: 'src/app.cpp'", StringComparison.Ordinal), again.RebuiltFromClean);
    }

    /// <summary>
    /// A build stopped part way - by its caller's time limit, or anything else that ends it - reaches no
    /// verdict and leaves only what it compiled. The record written as it began is what the next
    /// build dates its changes from, so that build carries on from what the stopped one compiled,
    /// and still starts from clean for a change dated before it. Dated from the build before it
    /// instead, a build stopped for running long would start from clean every time, and never
    /// finish.
    /// </summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(1.5, true)]
    public async Task ABuildStoppedPartWay_LeavesTheRecordItBeganWith_ForTheNextToDateAgainst(double editedAt, bool rebuilds)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await EditAsync(temp, "VERSION", "2.0.0\n", HoursFromNow(1), token);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stopping = Service(factory, exitCode: 0, phases: new ScriptedPhases(building: limit.Cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.BuildAsync(Config(), request, limit.Token));

        // What the stopped build compiled before it was stopped.
        WriteBuilt(request, temp, "obj/version.o", HoursFromNow(2));
        await EditAsync(temp, "src/app.cpp", "int main(){ return 0; }\n", HoursFromNow(editedAt), token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.Equal(rebuilds, again.RebuiltFromClean is not null);
        Assert.True(!rebuilds || again.RebuiltFromClean!.StartsWith("a change a build system could miss: 'src/app.cpp'", StringComparison.Ordinal), again.RebuiltFromClean);
    }

    /// <summary>
    /// An input deleted since the build is left to the build system, which sees an input gone
    /// without asking its date. It is not asked here either: a file that is not there has no date to
    /// give.
    /// </summary>
    [Fact]
    public async Task AnInputDeletedSinceTheBuild_IsLeftToTheBuildSystem()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        File.Delete(temp.Combine("VERSION"));

        Assert.Null((await BuildOnceAsync(factory, request, token)).RebuiltFromClean);
    }

    /// <summary>
    /// After a build that never finished, and so recorded no newest file, the directory is read for its
    /// own; one that cannot be read leaves nothing to date a change against, which rebuilds from clean
    /// for that reason alone, and says so. The decision reads it only where an input changed, so where
    /// none did the directory is kept, and the one walk of it is the build's own, as it ends.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnreadableBuildDirectory_RebuildsFromClean_OnlyWhereAnInputChanged(bool changed)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);
        await StopPartWayAsync(factory, request, token);

        if (changed)
        {
            await EditAsync(temp, "src/app.cpp", "changed\n", HoursFromNow(1), token);
        }

        var unreadable = new UnreadableBuildDirectory(factory.FileSystem, request.Variant.DirectoryUnder(temp.Path));
        var again = await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, fileSystem: unreadable));

        if (changed)
        {
            Assert.NotNull(again.RebuiltFromClean);
            Assert.StartsWith("an unreadable build directory: 'src/app.cpp' changed", again.RebuiltFromClean, StringComparison.Ordinal);
            Assert.Contains(UnreadableBuildDirectory.Refusal.TrimEnd('.'), again.RebuiltFromClean, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(again.RebuiltFromClean);
        }

        // The decision's walk where an input changed, and the build's own as it ended.
        Assert.Equal(changed ? 2 : 1, unreadable.Walks);
    }

    /// <summary>
    /// A changed input whose date cannot be read leaves nothing to say whether the build system will
    /// see the change, which rebuilds from clean for that reason alone, and says so.
    /// </summary>
    [Fact]
    public async Task AChangedInputThatCannotBeDated_RebuildsFromClean_SayingSo()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await EditAsync(temp, "src/app.cpp", "changed\n", HoursFromNow(1), token);

        var undatable = Service(factory, exitCode: 0, fileSystem: new UndatableInput(factory.FileSystem, "src/app.cpp"));
        var again = await BuildOnceAsync(factory, request, token, undatable);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("an undated input: 'src/app.cpp' changed", again.RebuiltFromClean, StringComparison.Ordinal);
        Assert.Contains(UndatableInput.Refusal.TrimEnd('.'), again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A link the build made to an input is dated as the link, when the build made it, never as the
    /// input it points at: a build that links its test data in from the source tree would otherwise
    /// date every edit to that data against the edit itself, and rebuild from clean for each one.
    /// Asked after a build that never finished, whose directory is read as it stands, which is where a
    /// link could be read as what it points at.
    /// </summary>
    [Fact]
    public async Task ALinkTheBuildMadeToAnInput_IsDatedAsTheLink_NotAsTheInput()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        try
        {
            File.CreateSymbolicLink(Path.Combine(request.Variant.DirectoryUnder(temp.Path), "VERSION"), temp.Combine("VERSION"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        await StopPartWayAsync(factory, request, token);
        await EditAsync(temp, "VERSION", "2.0.0\n", HoursFromNow(1), token);

        Assert.Null((await BuildOnceAsync(factory, request, token)).RebuiltFromClean);
    }

    /// <summary>
    /// The guards open on the reading the decision took, not on another taken moments later: a file
    /// changed between two readings would be what the build records it was given, compared and dated
    /// by neither. So an input is read once as the build begins and once as it ends.
    /// </summary>
    [Fact]
    public async Task TheGuards_OpenOnTheReadingTheDecisionTook()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        var counting = new CountingReads(factory.FileSystem, "src/app.cpp");

        Assert.Null((await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, fileSystem: counting))).RebuiltFromClean);
        Assert.Equal(2, counting.Reads);
    }

    /// <summary>
    /// After a build that finished, its guards vouched for the tree while it ran, so an input written
    /// again since with the same content - dated before what the build left, as a tool that keeps a
    /// file's old time writes it - is no change at all, and keeps the directory.
    /// </summary>
    [Fact]
    public async Task AnInputRewrittenAsItWasAfterAFinishedBuild_IsNoChange()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await EditAsync(temp, "src/app.cpp", await File.ReadAllTextAsync(temp.Combine("src", "app.cpp"), token), HoursFromNow(-2), token);

        Assert.Null((await BuildOnceAsync(factory, request, token)).RebuiltFromClean);
    }

    /// <summary>
    /// A build stopped part way says nothing of whether its tree held still, and one that moved and
    /// came back - a stash and its pop, the compiler reading the file in between and the object it
    /// wrote dated after both - leaves every input as the record has it. Written again since the build
    /// began, the file counts as changed, and is dated against what that build left.
    /// </summary>
    [Fact]
    public async Task AnInputWrittenAgainDuringABuildThatNeverFinished_CountsAsChanged()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        var app = temp.Combine("src", "app.cpp");
        var committed = await File.ReadAllTextAsync(app, token);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stashing = Service(factory, exitCode: 0, phases: new ScriptedPhases(building: () =>
        {
            File.WriteAllText(app, "int main(){ return 2; }\n");
            File.WriteAllText(app, committed);
            WriteBuilt(request, temp, "obj/app.o", DateTime.UtcNow.AddMinutes(1));
            limit.Cancel();
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stashing.BuildAsync(Config(), request, limit.Token));

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("a change a build system could miss: 'src/app.cpp' was written again after the last build began", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A phase that spans a clock step marks the record at once, not only as the build ends: a build
    /// stopped in the phase after it would otherwise leave a record saying nothing of the step, and
    /// the next build would carry on from objects that cannot be ordered.
    /// </summary>
    [Fact]
    public async Task AStepInAPhaseBeforeTheBuildIsStopped_StillStartsTheNextFromClean()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var clock = new SteppingClock();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stepping = Service(
            factory,
            exitCode: 0,
            phases: new ScriptedPhases(configuring: () => clock.Step(TimeSpan.FromSeconds(25)), building: limit.Cancel),
            wallClock: clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stepping.BuildAsync(Config(), request, limit.Token));

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("a clock step: the previous build's 'configure' phase spanned one", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compiler updated in place - the same path, another version, as a Visual Studio update rewrites
    /// cl.exe where it stands - starts the directory from clean, naming both versions: CMake loads its
    /// record of the old one on every configure, and a consumer's first build after one failed every
    /// precompiled header it had. The version CMake recorded keeps the directory.
    /// </summary>
    [Theory]
    [InlineData("195136260", true)]
    [InlineData("195136257", false)]
    public async Task ACompilerUpdatedInPlace_StartsTheDirectoryFromClean_NamingBothVersions(string fullVersion, bool rebuilds)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var cl = temp.WriteFile(Path.Combine(ToolchainDirectory, "cl.exe"), "a compiler").Replace('\\', '/');

        await BuildOnceAsync(factory, request, token);
        IdentifiedAs(request, temp, cl, "19.51.36257.0");

        var answering = new VersionAnswering($"1951 {fullVersion} 0");
        var built = await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, compilers: answering));

        Assert.Equal(rebuilds, built.RebuiltFromClean is not null);
        Assert.True(
            !rebuilds || built.RebuiltFromClean!.StartsWith(
                $"a changed compiler: CMake identified CXX's compiler, '{cl}', as MSVC 19.51.36257.0, and it is 19.51.36260.0 now",
                StringComparison.Ordinal),
            built.RebuiltFromClean);

        // Asked as CMake runs it, in the build's environment, over a line written beside the leg's logs.
        var asked = Assert.Single(answering.Requests);
        Assert.Equal(cl, asked.FileName);
        Assert.Equal(["/nologo", "/EP", Path.Combine(request.RunDirectory, Leg, "compiler-version-CXX.cpp")], asked.Arguments);
    }

    /// <summary>
    /// A compiler CMake identified that is not there now - its toolset removed, or moved - starts the
    /// directory from clean too, since every configure would load a record of a program that is gone.
    /// </summary>
    [Fact]
    public async Task ACompilerThatIsGone_StartsTheDirectoryFromClean()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var gone = temp.Combine(ToolchainDirectory, "removed", "cl.exe").Replace('\\', '/');

        await BuildOnceAsync(factory, request, token);
        IdentifiedAs(request, temp, gone, "19.51.36257.0");

        var built = await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, compilers: new VersionAnswering("unused")));

        Assert.NotNull(built.RebuiltFromClean);
        Assert.StartsWith($"a changed compiler: CMake identified CXX's compiler, '{gone}', as MSVC 19.51.36257.0, and it is not there now", built.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compiler that cannot be asked is said and passed over: the question names the cause of a failure
    /// the build would show anyway, and one that cannot be put is no reason to discard a warm directory.
    /// </summary>
    [Fact]
    public async Task ACompilerThatCannotBeAsked_IsSaid_AndKeepsTheDirectory()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var cl = temp.WriteFile(Path.Combine(ToolchainDirectory, "cl.exe"), "a compiler").Replace('\\', '/');

        await BuildOnceAsync(factory, request, token);
        IdentifiedAs(request, temp, cl, "19.51.36257.0");

        var built = await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, compilers: new QuietRunner(2)));

        Assert.Null(built.RebuiltFromClean);
        Assert.Contains("whether CXX's compiler is still the MSVC 19.51.36257.0 CMake identified could not be asked", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A compiler CMake could not identify - recorded with no id and no version - is neither asked nor
    /// said: nothing it answered could be held to what CMake recorded, and a warning on every build would
    /// be about a question nobody puts.
    /// </summary>
    [Fact]
    public async Task ACompilerCMakeCouldNotIdentify_IsNeitherAskedNorSaid()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var cl = temp.WriteFile(Path.Combine(ToolchainDirectory, "cl.exe"), "a compiler").Replace('\\', '/');

        await BuildOnceAsync(factory, request, token);
        IdentifiedAs(request, temp, cl, version: string.Empty, id: string.Empty);

        var answering = new VersionAnswering("1951 195136260 0");
        var built = await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, compilers: answering));

        Assert.Null(built.RebuiltFromClean);
        Assert.Empty(answering.Requests);
        Assert.DoesNotContain("could not be asked", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a project CMake configures is asked: nothing else keeps a record of a compiler it will not
    /// identify again, and a record lying in another kind's directory says nothing about its build.
    /// </summary>
    [Fact]
    public async Task AProjectCMakeDoesNotConfigure_NeverAsksAnyCompiler()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var request = Request(temp, outputs: ["bin/app.dll"]);
        var (_, factory) = await TrackedWithFactoryAsync(temp, token);
        var cl = temp.WriteFile(Path.Combine(ToolchainDirectory, "cl.exe"), "a compiler").Replace('\\', '/');

        IdentifiedAs(request, temp, cl, "19.51.36257.0");

        var answering = new VersionAnswering("1951 195136260 0");

        await Service(factory, exitCode: 0, compilers: answering).BuildAsync(Config(), request, token);

        Assert.Empty(answering.Requests);
    }

    /// <summary>
    /// A build during one of whose phases the wall clock stepped stamped objects that cannot be
    /// ordered against anything since, and the next build starts from clean, naming the phase.
    /// </summary>
    [Fact]
    public async Task ABuildThatSpannedAClockStep_StartsTheNextFromClean_NamingThePhase()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var clock = new SteppingClock();
        var stepping = Service(factory, exitCode: 0, phases: new ScriptedPhases(building: () => clock.Step(TimeSpan.FromSeconds(25))), wallClock: clock);

        Assert.Contains((await BuildOnceAsync(factory, request, token, stepping)).Phases, phase => phase.ClockStepped);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("a clock step: the previous build's 'build' phase spanned one", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build whose inputs moved while it ran compiled a tree no record describes, and the next
    /// build starts from clean - saying that, and not a clock step that never happened.
    /// </summary>
    [Fact]
    public async Task ABuildWhoseInputsMovedWhileItRan_StartsTheNextFromClean_SayingWhy()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        var editing = Service(
            factory,
            exitCode: 0,
            phases: new ScriptedPhases(building: () => File.WriteAllText(temp.Combine("src", "app.cpp"), "int main(){ return 1; }\n")));

        Assert.Equal(LegVerdict.InputsMoved, (await BuildOnceAsync(factory, request, token, editing)).Verdict.Verdict);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("a moving tree:", again.RebuiltFromClean, StringComparison.Ordinal);
        Assert.Contains("src/app.cpp", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build that could not be shown to have had its directory to itself - here because the
    /// machine's process table could not be read - leaves objects nobody can vouch for, and the next
    /// build starts from clean, saying so rather than naming a clock step.
    /// </summary>
    [Fact]
    public async Task ABuildNotShownToHaveHadItsDirectoryToItself_StartsTheNextFromClean_SayingWhy()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var blind = Service(factory, exitCode: 0, processTable: new UnreadableProcessTable());

        Assert.Equal(LegVerdict.Unmeasured, (await BuildOnceAsync(factory, request, token, blind)).Verdict.Verdict);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("a shared build directory:", again.RebuiltFromClean, StringComparison.Ordinal);
        Assert.Contains(UnreadableProcessTable.Blocked, again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// An input that cannot be read says nothing about whether the tree held still, and the build
    /// starts from clean. The record it writes, having begun without reading that input, is marked,
    /// so the build after it starts from clean too: a record short of one input would compare that
    /// input as though it had held still. Marked as the build begins, so one stopped part way leaves
    /// the mark as surely as one that finished.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnreadableInput_StartsFromClean_AndMarksTheRecordItBeganWithout(bool stopped)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        var unreadable = new UnreadableInput(factory.FileSystem, "src/app.cpp");

        if (stopped)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stopping = Service(factory, exitCode: 0, phases: new ScriptedPhases(building: limit.Cancel), fileSystem: unreadable);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.BuildAsync(Config(), request, limit.Token));
        }
        else
        {
            var unread = await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, fileSystem: unreadable));

            Assert.NotNull(unread.RebuiltFromClean);
            Assert.StartsWith("an unreadable input: 'src/app.cpp'", unread.RebuiltFromClean, StringComparison.Ordinal);
        }

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("an unrecorded input: 'src/app.cpp'", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A record holding no fingerprint - written before fingerprinting was, or when nothing could be
    /// fingerprinted - says nothing about whether the tree held still, and the next build starts
    /// from clean.
    /// </summary>
    [Fact]
    public async Task ARecordWithNoFingerprint_StartsTheNextFromClean()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        await File.WriteAllTextAsync(RecordOf(request, temp), $"clean\n{request.Variant.DirectoryName}\n", token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("no record to compare:", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A directory that holds files and no record starts from clean, naming one of them: a clean start
    /// stopped part way through its delete can take the record and leave the objects it was there to
    /// discard, and a configure run by hand leaves a build nothing recorded. One that holds nothing, or is
    /// not there, has nothing to discard.
    /// </summary>
    [Theory]
    [InlineData("a file", true)]
    [InlineData("nothing", false)]
    [InlineData("no directory", false)]
    public async Task ADirectoryWithNoRecord_StartsFromClean_WhereItHoldsFiles(string holds, bool rebuilds)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        if (holds != "no directory")
        {
            Directory.CreateDirectory(Path.Combine(request.Variant.DirectoryUnder(temp.Path), "obj"));
        }

        if (holds == "a file")
        {
            WriteBuilt(request, temp, "obj/app.o", HoursFromNow(-1));
        }

        var built = await BuildOnceAsync(factory, request, token);

        Assert.Equal(LegVerdict.Passed, built.Verdict.Verdict);
        Assert.Equal(rebuilds, built.RebuiltFromClean is not null);
        Assert.True(
            !rebuilds || built.RebuiltFromClean!.StartsWith(
                "no record to compare: the directory holds files - 'obj/app.o' among them - and no record of what they were built from",
                StringComparison.Ordinal),
            built.RebuiltFromClean);
    }

    /// <summary>
    /// A directory with no record that cannot be read for whether it holds anything leaves nothing to say
    /// it holds nothing, which starts it from clean for that reason alone, and says so.
    /// </summary>
    [Fact]
    public async Task ADirectoryWithNoRecordThatCannotBeRead_StartsFromClean_SayingSo()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);
        var directory = request.Variant.DirectoryUnder(temp.Path);

        Directory.CreateDirectory(directory);

        var unreadable = new UnreadableBuildDirectory(factory.FileSystem, directory);
        var built = await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, fileSystem: unreadable));

        Assert.NotNull(built.RebuiltFromClean);
        Assert.StartsWith("an unreadable build directory: it holds no record, and whether it holds anything else could not be read", built.RebuiltFromClean, StringComparison.Ordinal);
        Assert.Contains(UnreadableBuildDirectory.Refusal.TrimEnd('.'), built.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the decision discards the directory, the guards read the tree afresh once it is gone, not
    /// on the decision's own reading: an input unreadable only as the decision read it - held open a
    /// moment by another process - is read for the clean build, which passes and leaves a record the next
    /// build keeps its directory by. On the decision's reading, the clean build was unmeasured and its
    /// record marked, and the next build started from clean again.
    /// </summary>
    [Fact]
    public async Task AnInputUnreadableOnlyAsTheDecisionReadIt_IsReadAfreshForTheCleanBuild()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        var once = new UnreadableInput(factory.FileSystem, "src/app.cpp", refusals: 1);
        var clean = await BuildOnceAsync(factory, request, token, Service(factory, exitCode: 0, fileSystem: once));

        Assert.NotNull(clean.RebuiltFromClean);
        Assert.StartsWith("an unreadable input: 'src/app.cpp'", clean.RebuiltFromClean, StringComparison.Ordinal);
        Assert.Equal(LegVerdict.Passed, clean.Verdict.Verdict);
        Assert.Null((await BuildOnceAsync(factory, request, token)).RebuiltFromClean);
    }

    /// <summary>
    /// A record 0.5.8 marked unordered - which said so, and never why - still starts the next build
    /// from clean: whatever made it so stamped the objects it describes.
    /// </summary>
    [Fact]
    public async Task ARecordAnEarlierVersionMarkedUnordered_StillStartsTheNextFromClean()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        var record = RecordOf(request, temp);

        await File.WriteAllTextAsync(record, EarlierRecord.Written(await File.ReadAllTextAsync(record, token), unordered: true), token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.StartsWith("an unordered build:", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clean record 0.5.8 wrote, which says neither when each input was written nor which file its
    /// build left newest, is read as one a build that never finished left: an input whose content held
    /// keeps the directory, and one that changed is dated against the directory as it stands - kept where
    /// dated after it, started from clean where dated no later.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(1.0, false)]
    [InlineData(-2.0, true)]
    public async Task ACleanRecordAnEarlierVersionWrote_IsReadAsOneABuildThatNeverFinishedLeft(double? editedAt, bool rebuilds)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        var record = RecordOf(request, temp);

        await File.WriteAllTextAsync(record, EarlierRecord.Written(await File.ReadAllTextAsync(record, token), unordered: false), token);

        if (editedAt is { } hours)
        {
            await EditAsync(temp, "src/app.cpp", "changed\n", HoursFromNow(hours), token);
        }

        var again = await BuildOnceAsync(factory, request, token);

        Assert.Equal(rebuilds, again.RebuiltFromClean is not null);
        Assert.True(
            !rebuilds || again.RebuiltFromClean!.StartsWith(
                "a change a build system could miss: 'src/app.cpp' differs from what this directory was last given to build",
                StringComparison.Ordinal),
            again.RebuiltFromClean);
        Assert.True(!rebuilds || again.RebuiltFromClean!.Contains("the newest file in the directory", StringComparison.Ordinal), again.RebuiltFromClean);
    }

    /// <summary>
    /// Only the record's first line says whether it is unordered. Every later one names an input,
    /// and an input can be called anything: read from the whole record, a source whose path held the
    /// mark rebuilt its variant from clean on every build.
    /// </summary>
    [Fact]
    public async Task AnInputNamedLikeTheMark_IsNotReadAsOne()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        await File.WriteAllTextAsync(temp.Combine("src", "clock-stepped.cpp"), "int stepped;\n", token);
        await factory.CommitAllAsync(temp.Path, "a source named like the mark", token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);
        Assert.Null((await BuildOnceAsync(factory, request, token)).RebuiltFromClean);
    }

    /// <summary>
    /// Declared and non-empty, the project's own list replaces the one its type would use. Empty is
    /// "say nothing", not "match nothing": a list matching nothing compares equal every time, which
    /// is exactly the answer that keeps a stale binary.
    /// </summary>
    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { ".md" }, true)]
    public async Task RebuildableFormats_ReplaceTheLanguageSet_OnlyWhenTheySaySomething(
        string[] formats,
        bool rebuilds)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token, formats);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, token)).Verdict.Verdict);

        // Dated before the build, so only whether the file is an input decides.
        await EditAsync(temp, "docs/guide.md", "rewritten\n", HoursFromNow(-2), token);

        var again = await BuildOnceAsync(factory, request, token);

        Assert.Equal(rebuilds, again.RebuiltFromClean is not null);
    }

    /// <summary>
    /// A tree git tracks, holding one source, one document and one extensionless file a build
    /// reads, with a cmake project built out of source.
    /// </summary>
    private static async Task<(HarnessFactory Factory, BuildRequest Request)> TrackedTreeAsync(
        TempDirectory temp,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? formats = null)
    {
        var factory = new HarnessFactory();

        Directory.CreateDirectory(temp.Combine("src"));
        Directory.CreateDirectory(temp.Combine("docs"));
        await File.WriteAllTextAsync(temp.Combine("src", "app.cpp"), "int main(){}\n", cancellationToken);
        await File.WriteAllTextAsync(temp.Combine("docs", "guide.md"), "how to\n", cancellationToken);
        await File.WriteAllTextAsync(temp.Combine("VERSION"), "1.0.0\n", cancellationToken);

        await factory.InitializeHarnessAsync(temp.Path, cancellationToken, new HarnessConfig());
        await factory.CommitAllAsync(temp.Path, "initial", cancellationToken);

        var request = new BuildRequest(
            Leg,
            temp.Path,
            new ProjectConfig
            {
                Name = "app",
                Type = "cmake",
                Path = ".",
                BuildOutputs = [(BuildOutput)"bin/app"],
                RebuildableFormats = [.. formats ?? []],
            },
            new VariantKey("x86_64", "gcc", "debug", null),
            PlatformNames.Linux,
            Cores: 2,
            RunDirectory: temp.Combine(".harness-config", "runs", "20260916-100000-0a1b2c3d"));

        return (factory, request);
    }

    /// <summary>
    /// Builds once, with the service given or else one whose every phase exits 0; either leaves the
    /// output as its build phase runs, unless told otherwise, so the build is witnessed.
    /// </summary>
    private static Task<BuildResult> BuildOnceAsync(
        HarnessFactory factory,
        BuildRequest request,
        CancellationToken cancellationToken,
        BuildService? service = null)
        => (service ?? Service(factory, exitCode: 0)).BuildAsync(Config(), request, cancellationToken);

    /// <summary>Now, moved by <paramref name="hours"/>: what a test dates an edit or an output by.</summary>
    private static DateTime HoursFromNow(double hours) => DateTime.UtcNow.AddHours(hours);

    /// <summary>
    /// Rewrites one tracked file and dates it: after the build it follows, as an edit is, or before
    /// it, as a stepped clock or a tool that keeps a file's old time leaves one.
    /// </summary>
    private static async Task EditAsync(
        TempDirectory temp,
        string relativePath,
        string content,
        DateTime dated,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));

        await File.WriteAllTextAsync(path, content, cancellationToken);
        File.SetLastWriteTimeUtc(path, dated);
    }

    /// <summary>
    /// Writes a file into the build directory, dated as whatever wrote it left it: a phase compiling it,
    /// or something run after the build.
    /// </summary>
    private static void WriteBuilt(BuildRequest request, TempDirectory temp, string relativePath, DateTime dated)
    {
        var path = Path.Combine(request.Variant.DirectoryUnder(temp.Path), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "compiled");
        File.SetLastWriteTimeUtc(path, dated);
    }

    /// <summary>
    /// A build stopped as its build phase starts, as its caller's time limit stops one: it leaves the record
    /// it began with, and no newest file.
    /// </summary>
    private static async Task StopPartWayAsync(HarnessFactory factory, BuildRequest request, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(factory, exitCode: 0, phases: new ScriptedPhases(building: limit.Cancel)).BuildAsync(Config(), request, limit.Token));
    }

    /// <summary>
    /// Writes what CMake 4.3 leaves in a build directory it identified a C++ compiler for: the index of
    /// its answer, naming the version, and its record of identifying the compiler at
    /// <paramref name="program"/> as <paramref name="id"/> <paramref name="version"/> - both empty, as CMake
    /// records a compiler it could not identify.
    /// </summary>
    private static void IdentifiedAs(BuildRequest request, TempDirectory temp, string program, string version, string id = "MSVC")
    {
        var build = request.Variant.DirectoryUnder(temp.Path);
        var reply = Path.Combine(build, ".cmake", "api", "v1", "reply", "index-2026-09-23T08-00-00-0000.json");
        var record = Path.Combine(build, "CMakeFiles", "4.3.0", "CMakeCXXCompiler.cmake");

        Directory.CreateDirectory(Path.GetDirectoryName(reply)!);
        Directory.CreateDirectory(Path.GetDirectoryName(record)!);
        File.WriteAllText(reply, """{ "cmake": { "version": { "string": "4.3.0" } }, "reply": {} }""");
        File.WriteAllText(record, $"""
            set(CMAKE_CXX_COMPILER "{program}")
            set(CMAKE_CXX_COMPILER_ARG1 "")
            set(CMAKE_CXX_COMPILER_ID "{id}")
            set(CMAKE_CXX_COMPILER_VERSION "{version}")
            set(CMAKE_CXX_COMPILER_FRONTEND_VARIANT "{id}")
            """);
    }

    /// <summary>The record a build leaves beside itself, which the next build reads.</summary>
    private static string RecordOf(BuildRequest request, TempDirectory temp)
        => Path.Combine(request.Variant.DirectoryUnder(temp.Path), ".harness-build");

    /// <summary>
    /// A service whose tree git can be asked about. A build reads the tree while it runs, so a tree
    /// nothing can list is a build nobody watched, and the leg is unmeasured rather than passed —
    /// the same rule the test verb has always applied. Every build here needs a repository for that
    /// reason and not because these tests are about git.
    /// </summary>
    private static async Task<BuildService> TrackedAsync(
        TempDirectory temp,
        CancellationToken cancellationToken,
        int exitCode = 0,
        IReadOnlyList<string>? leaves = null)
        => (await TrackedWithFactoryAsync(temp, cancellationToken, exitCode, leaves)).Service;

    /// <summary>The same, with the factory, for a test that reads what the build said.</summary>
    private static async Task<(BuildService Service, HarnessFactory Factory)> TrackedWithFactoryAsync(
        TempDirectory temp,
        CancellationToken cancellationToken,
        int exitCode = 0,
        IReadOnlyList<string>? leaves = null,
        IProcessRunner? deadOutputs = null)
    {
        var factory = new HarnessFactory();

        await factory.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        // A source the project's own type reads, so the guards are actually on in these tests: a
        // tree tracking nothing this build reads has nothing to watch and would exercise none of it.
        await File.WriteAllTextAsync(temp.Combine("src.cs"), "class App;" + Environment.NewLine, cancellationToken);
        await factory.CommitAllAsync(temp.Path, "initial", cancellationToken);

        return (Service(factory, exitCode, leaves: leaves, deadOutputs: deadOutputs), factory);
    }

    /// <summary>
    /// A compiler a survey found off the PATH is the one the build starts, from the directory
    /// appended to it, so that is the file the directory is held to: one configured with a gcc
    /// elsewhere is refused, though the names match.
    /// </summary>
    [Fact]
    public async Task ACompilerFoundInAProgramDirectory_IsTheOneTheDirectoryIsHeldTo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);

        temp.WriteProgram(ToolchainDirectory, "gcc");
        var elsewhere = temp.WriteProgram("elsewhere-bin", "gcc");
        Directory.CreateDirectory(temp.Combine("empty-path"));

        var request = tracked with
        {
            HostEnvironment = new Dictionary<string, string> { ["CC"] = "gcc", ["PATH"] = temp.Combine("empty-path") },
            ProgramDirectories = [temp.Combine(ToolchainDirectory)],
        };

        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);
        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH={elsewhere.Replace('\\', '/')}\n",
            cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(factory, exitCode: 0).BuildAsync(Config(), request, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains($"which starts '{temp.Combine(ToolchainDirectory, OperatingSystem.IsWindows() ? "gcc.exe" : "gcc")}' now", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every configure is asked which compilers it resolved, and the build names what it answered:
    /// the compilers the leg's verdict came from, whatever that verdict is.
    /// </summary>
    [Fact]
    public async Task TheBuild_AsksCMakeWhichCompilersItResolved_AndNamesThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, request) = await TrackedTreeAsync(temp, cancellationToken);
        var configure = new ConfiguringRunner("GNU", "13.2.0");

        var result = await Service(factory, exitCode: 0, phases: configure).BuildAsync(Config(), request, cancellationToken);

        Assert.True(configure.Asked, "configure ran without the query that makes CMake answer");
        Assert.Equal([new CompilerFact("C", "GNU", "13.2.0"), new CompilerFact("CXX", "GNU", "13.2.0")], result.Compilers);
    }

    /// <summary>
    /// A compiler CMake configured the build with that contradicts the toolchain's compilerId fails
    /// the leg before anything is built with it, naming both.
    /// </summary>
    [Fact]
    public async Task ACompilerContradictingTheToolchain_FailsTheLeg_BeforeAnythingIsBuilt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, request) = await TrackedTreeAsync(temp, cancellationToken);
        var configure = new ConfiguringRunner("GNU", "13.2.0");

        var result = await Service(factory, exitCode: 0, phases: configure).BuildAsync(Declaring(("C", "MSVC"), ("CXX", "MSVC")), request, cancellationToken);

        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);
        Assert.Contains(
            "CMake configured this build with another compiler than toolchain 'gcc' declares: C with GNU 13.2.0, not MSVC; CXX with GNU 13.2.0, not MSVC",
            result.Verdict.Detail,
            StringComparison.Ordinal);
        Assert.DoesNotContain(configure.Started, arguments => arguments.Contains("--build"));
        Assert.NotEmpty(result.Compilers);
    }

    /// <summary>
    /// A declared compiler CMake named nothing for is a fact nothing established, never a pass: the
    /// leg is unwitnessed, naming the language and why CMake said nothing.
    /// </summary>
    [Fact]
    public async Task ADeclaredCompilerCMakeNamedNothingFor_IsUnwitnessed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, request) = await TrackedTreeAsync(temp, cancellationToken);

        var result = await Service(factory, exitCode: 0).BuildAsync(Declaring(("C", "GNU")), request, cancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains(
            "toolchain 'gcc' declares the compiler for C, and CMake named none for it: this configure wrote no file API answer",
            result.Verdict.Detail,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A configure that fails names no compiler an earlier configure resolved. CMake leaves that answer
    /// where it was and writes an error index of its own; read as the newest answer, the earlier one
    /// named a leg whose compiler had changed by the compiler before it.
    /// </summary>
    [Fact]
    public async Task AFailedConfigure_NamesNoCompilerAnEarlierConfigureResolved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, request) = await TrackedTreeAsync(temp, cancellationToken);

        var first = await Service(factory, exitCode: 0, phases: new ConfiguringRunner("GNU", "13.2.0")).BuildAsync(Config(), request, cancellationToken);
        var failed = await Service(factory, exitCode: 1, phases: new ConfiguringRunner("Clang", "17.0.6", exitCode: 1))
            .BuildAsync(Declaring(("C", "Clang")), request, cancellationToken);

        Assert.NotEmpty(first.Compilers);
        Assert.Equal(LegVerdict.Failed, failed.Verdict.Verdict);
        Assert.Empty(failed.Compilers);
    }

    /// <summary>The compiler the toolchain declares, as CMake spells it or not, passes the build through.</summary>
    [Fact]
    public async Task TheDeclaredCompiler_PassesTheBuildThrough()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, request) = await TrackedTreeAsync(temp, cancellationToken);

        var result = await Service(factory, exitCode: 0, phases: new ConfiguringRunner("GNU", "13.2.0"))
            .BuildAsync(Declaring(("C", "gnu"), ("CXX", "GNU")), request, cancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    /// <summary>A configuration whose gcc toolchain declares <paramref name="ids"/> as its compilerId.</summary>
    private static HarnessConfig Declaring(params (string Language, string Id)[] ids)
    {
        var config = Config();
        var toolchain = new ToolchainConfig { Env = { ["CC"] = "gcc" } };

        foreach (var (language, id) in ids)
        {
            toolchain.CompilerId[language] = id;
        }

        config.Toolchains["gcc"] = toolchain;

        return config;
    }

    /// <summary>
    /// A CMake that answers the file API query on configure, as CMake 4.3 does, with
    /// <paramref name="id"/> for C and C++ - or, where it fails with <paramref name="exitCode"/>, with
    /// the error index CMake 4.3 writes in its place - and every other phase starts nothing.
    /// </summary>
    private sealed class ConfiguringRunner(string id, string version, int exitCode = 0) : IProcessRunner
    {
        /// <summary>How many answers every configure so far wrote, which names each one, as CMake's moment of writing does.</summary>
        private static int _written;

        private readonly List<IReadOnlyList<string>> _started = [];

        /// <summary>The arguments of every phase started, in order.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Started => _started;

        /// <summary>Whether configure found the query in its build directory when it ran.</summary>
        public bool Asked { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            _started.Add(request.Arguments);

            if (request.Arguments is ["-S", _, "-B", var build, ..])
            {
                Asked = File.Exists(Path.Combine(build, ".cmake", "api", "v1", "query", "toolchains-v1"));

                var replies = Path.Combine(build, ".cmake", "api", "v1", "reply");
                var moment = $"2026-09-19T16-16-05-{Interlocked.Increment(ref _written):D4}";
                Directory.CreateDirectory(replies);

                if (exitCode != 0)
                {
                    File.WriteAllText(
                        Path.Combine(replies, $"error-{moment}.json"),
                        """{ "reply": { "toolchains-v1": { "error": "no buildsystem generated" } } }""");
                }
                else
                {
                    File.WriteAllText(
                        Path.Combine(replies, $"index-{moment}.json"),
                        $$"""{ "reply": { "toolchains-v1": { "jsonFile": "toolchains-v1-{{moment}}.json" } } }""");
                    File.WriteAllText(
                        Path.Combine(replies, $"toolchains-v1-{moment}.json"),
                        $$"""{ "toolchains": [ { "language": "C", "compiler": { "id": "{{id}}", "version": "{{version}}" } }, { "language": "CXX", "compiler": { "id": "{{id}}", "version": "{{version}}" } } ] }""");
                }
            }

            return Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>Where a test puts the compilers its build's PATH names, outside anything the build reads.</summary>
    private const string ToolchainDirectory = "toolchain-bin";

    /// <summary>What a tracked tree's project declares its build produces, and what a build leaves unless told otherwise.</summary>
    private const string App = "bin/app";

    /// <summary>
    /// A build service over <paramref name="factory"/>, whose phases exit with
    /// <paramref name="exitCode"/> unless another runner is given, and whose build phase leaves
    /// <paramref name="leaves"/> - <see cref="App"/> unless told otherwise - below the directory it
    /// builds; <paramref name="fileSystem"/> is what the service and its fingerprints read the tree and
    /// the build directory through, <paramref name="wallClock"/> the clock its phases are timed against,
    /// <paramref name="compilers"/> what answers a compiler asked its version, and
    /// <paramref name="processTable"/> the machine its guards find - a quiet one unless told otherwise.
    /// </summary>
    /// <remarks>
    /// The phases are timed against a clock that steps only when a test steps it, never this machine's
    /// own. Every test here but the two about a step is about some other reason to start a build
    /// directory from clean, and on a host whose wall clock steps - WSL's stepped back 24.8 seconds and
    /// forward again every five - a phase is recorded as having spanned a step, which starts the next
    /// build from clean for that reason and tells the test nothing about the one it is checking.
    /// </remarks>
    private static BuildService Service(
        HarnessFactory factory,
        int exitCode,
        IProcessRunner? dependencies = null,
        IProcessRunner? phases = null,
        IProcessTable? processTable = null,
        IFileSystem? fileSystem = null,
        TimeProvider? wallClock = null,
        IProcessRunner? compilers = null,
        IReadOnlyList<string>? leaves = null,
        IProcessRunner? deadOutputs = null)
        => new(
            new PhaseRunner(new Leaving(phases ?? new QuietRunner(exitCode), leaves ?? [App]), factory.FileSystem, factory.Output, wallClock ?? new SteppingClock()),
            new BuildDirectoryGuard(factory.FileSystem, factory.Platform, factory.FilePermissions),
            new CMakeToolchainReader(factory.FileSystem),
            new NinjaDependencyCheck(dependencies ?? new QuietRunner(exitCode), factory.FileSystem),

            // Asked of its own runner: the dependency check's is counted by tests that ask it alone.
            new NinjaDeadOutputCheck(deadOutputs ?? new QuietRunner(1), factory.FileSystem),
            new CompilerVersionProbe(compilers ?? new QuietRunner(exitCode), fileSystem ?? factory.FileSystem),
            new InputFingerprint(fileSystem ?? factory.FileSystem, factory.Platform),
            new ProcessSampler(processTable ?? new QuietProcessTable(), factory.Platform, factory.Output),
            factory.GitClient,
            fileSystem ?? factory.FileSystem,
            factory.Output,

            // The same clock its phases are timed against, so which build wrote a file is decided here by a
            // clock that steps only when a test steps it, never by this machine's own.
            wallClock ?? new SteppingClock());

    private static HarnessConfig Config() => new()
    {
        Defaults = new HarnessDefaults { StallSeconds = 0 },
    };

    /// <summary>
    /// The same target is not the same file everywhere, and a witness had to be able to say so:
    /// with one flat path per entry, a leg set spanning Windows and POSIX could not name a program
    /// that existed on both, so no build on any of those legs could be witnessed at all.
    /// </summary>
    [Theory]
    [InlineData("windows", "bin/app.exe")]
    [InlineData("linux", "bin/posix-app")]
    [InlineData("macos", "bin/posix-app")]
    public async Task AKeyedOutput_IsLookedForUnderThePlatformTheBuildRanOn(string platformKey, string expected)
    {
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken, leaves: [])).BuildAsync(
            Config(),
            Request(
                temp,
                [Keyed(("windows", "bin/app.exe"), ("all", "bin/posix-app"))],
                platformKey),
            TestContext.Current.CancellationToken);

        // Nothing was produced, so the refusal names the path it looked for — which is the point:
        // the reader has to be able to see which platform's spelling was checked.
        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains(expected, result.Verdict.Detail, StringComparison.Ordinal);

        // And only that platform's spelling: naming both would leave the reader to work out which
        // of them this leg was actually missing. The two stems differ so that this discriminates on
        // every row — 'bin/app' is a prefix of 'bin/app.exe', so it never could.
        Assert.DoesNotContain(
            platformKey == "windows" ? "bin/posix-app" : "bin/app.exe",
            result.Verdict.Detail,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An output that resolves to nothing here is dropped from the list this build is held to. All
    /// of them resolving to nothing passes having looked for no file at all; some of them doing so
    /// passes having checked part of the evidence the file declares, with nothing saying which part
    /// went unchecked. Two things stop a configuration reaching either, and this refuses both
    /// anyway: a green build nobody witnessed is what the whole mechanism exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOutputNamingNoPathForThisPlatform_IsUnwitnessed_NotSilentlyDropped(bool alsoDeclaresOneThatResolves)
    {
        using var temp = new TempDirectory();

        BuildOutput[] outputs = alsoDeclaresOneThatResolves
            ? [Keyed(("windows", "bin/app.exe")), (BuildOutput)"compile_commands.json"]
            : [Keyed(("windows", "bin/app.exe"))];

        // The second entry resolves and is found, so only the unresolved one can decide this.
        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken, leaves: ["compile_commands.json"])).BuildAsync(
            Config(),
            Request(temp, outputs, "linux"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains("naming no path for 'linux'", result.Verdict.Detail, StringComparison.Ordinal);

        // The entry that went unchecked is named, because which one it was is the first thing to ask.
        Assert.Contains("windows: bin/app.exe", result.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlainOutput_StillAppliesOnEveryPlatform()
    {
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken, leaves: ["compile_commands.json"])).BuildAsync(
            Config(),
            Request(temp, [(BuildOutput)"compile_commands.json"], "macos"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task AKeyedOutput_IsWitnessed_WhenThePlatformsOwnFileIsThere()
    {
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken, leaves: ["bin/app.exe"])).BuildAsync(
            Config(),
            Request(temp, [Keyed(("windows", "bin/app.exe"), ("all", "bin/app"))], "windows"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    private static BuildRequest Request(TempDirectory temp, IReadOnlyList<string> outputs)
        => Request(temp, [.. outputs.Select(output => (BuildOutput)output)], "windows");

    private static BuildRequest Request(TempDirectory temp, IReadOnlyList<BuildOutput> outputs, string platformKey)
        => new(
            Leg,
            temp.Path,
            new ProjectConfig
            {
                Name = "app",
                Type = "dotnet",
                Path = "src/app",
                BuildOutputs = [.. outputs],
            },
            new VariantKey("x86_64", "msvc", "release", null),
            platformKey,
            Cores: 2,
            RunDirectory: temp.Combine(".harness-config", "runs", "20260916-100000-0a1b2c3d"));

    /// <summary>An entry naming one path per platform.</summary>
    private static BuildOutput Keyed(params (string Platform, string Path)[] paths)
        => BuildOutput.Keyed(paths.Select(entry => new KeyValuePair<string, string>(entry.Platform, entry.Path)));

    /// <summary>
    /// The dependency records are read by the ninja the build ran, as its configuration recorded it:
    /// one only the build's own environment could find is found all the same, and reads the records
    /// the way it wrote them. Looked up by name instead, it was not there, and a green build failed.
    /// </summary>
    [Fact]
    public async Task TheDependencyRecords_AreReadByTheNinjaTheBuildRan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, request) = await TrackedTreeAsync(temp, cancellationToken);
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        // Whole on the machine that ran the build, written the way CMake writes it.
        var recorded = temp.Combine("toolchain", "bin", "ninja").Replace('\\', '/');

        // Built once, and configured as CMake would have configured it then: the directory is kept for
        // the build that follows, which reads what that configure wrote.
        await BuildOnceAsync(factory, request, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName), string.Empty, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path}\nCMAKE_MAKE_PROGRAM:FILEPATH={recorded}\n",
            cancellationToken);

        var dependencies = new RecordingRunner("app.o: #deps 1, deps mtime 1 (VALID)\n    app.h\n");

        _ = await Service(factory, exitCode: 0, dependencies).BuildAsync(Config(), request, cancellationToken);

        Assert.Equal(recorded, Assert.Single(dependencies.Started).FileName);
    }

    /// <summary>
    /// A host's own environment reaches every phase of the build and the ninja that reads its
    /// records, beneath the variant's: a name only the host sets is the host's, one the toolchain
    /// sets too is the toolchain's, and a compiler cache the host declares is keyed against the leg's
    /// own tree.
    /// </summary>
    [Fact]
    public async Task TheHostsEnvironment_ReachesEveryPhaseAndTheCheck_BeneathTheVariants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);
        var request = tracked with
        {
            HostEnvironment = new Dictionary<string, string>
            {
                ["RH_HOST"] = "host",
                ["RH_BOTH"] = "host",
                ["CCACHE_DIR"] = temp.Combine("cache"),
            },
        };
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        // Built once, and configured as CMake would have configured it then: the directory is kept for
        // the build that follows, whose dependency records are read.
        await BuildOnceAsync(factory, request, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName), string.Empty, cancellationToken);

        var config = Config();
        config.Toolchains["gcc"] = new ToolchainConfig { Platforms = [PlatformNames.Linux], Env = { ["RH_BOTH"] = "variant" } };

        var phases = new RecordingRunner(string.Empty);
        var dependencies = new RecordingRunner("app.o: #deps 1, deps mtime 1 (VALID)\n    app.h\n");

        _ = await Service(factory, exitCode: 0, dependencies, phases).BuildAsync(config, request, cancellationToken);

        Assert.NotEmpty(phases.Started);
        Assert.Single(dependencies.Started);

        foreach (var started in phases.Started.Concat(dependencies.Started))
        {
            Assert.Equal("host", started.Environment["RH_HOST"]);
            Assert.Equal("variant", started.Environment["RH_BOTH"]);
            Assert.Equal(temp.Path, started.Environment["CCACHE_BASEDIR"]);
        }
    }

    /// <summary>
    /// A compiler the host's env names is the one the build uses wherever the variant names none, so
    /// a directory CMake configured with another is refused, as it is for a variant's compiler -
    /// never reused with the compiler CMake cached, the leg passing on a compiler nobody chose.
    /// </summary>
    [Fact]
    public async Task ADirectoryConfiguredWithAnotherCompilerThanTheHostNames_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);
        var request = tracked with { HostEnvironment = new Dictionary<string, string> { ["CC"] = "clang-17" } };
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH=/usr/bin/gcc\n",
            cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory, exitCode: 0).BuildAsync(Config(), request, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("clang-17", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compiler the host names with words after it rebuilds the directory it configured: CMake
    /// cached ccache as the compiler and clang as its argument, and the guard reads the value the
    /// same way. Compared whole, 'ccache clang' against '/usr/bin/ccache' refused every rebuild, and
    /// the refusal ended the whole run.
    /// </summary>
    [Fact]
    public async Task ACompilerTheHostNamesWithWordsAfterIt_RebuildsTheDirectoryItConfigured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);

        // The ccache the build's PATH starts is the one the directory was configured with, so the
        // question is only what was recorded after it.
        var ccache = temp.WriteProgram(ToolchainDirectory, "ccache");
        var request = tracked with
        {
            HostEnvironment = new Dictionary<string, string> { ["CC"] = "ccache clang", ["PATH"] = temp.Combine(ToolchainDirectory) },
        };
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        // Built once, and configured as CMake would have configured it then.
        await BuildOnceAsync(factory, request, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH={ccache.Replace('\\', '/')}\nCMAKE_C_COMPILER_ARG1:STRING= clang\n",
            cancellationToken);

        var result = await Service(factory, exitCode: 0).BuildAsync(Config(), request, cancellationToken);

        Assert.NotEqual(LegVerdict.Poisoned, result.Verdict.Verdict);
        Assert.Null(result.RebuiltFromClean);
    }

    /// <summary>
    /// A toolchain that gives CMake its compiler as a cache variable builds with that one, whatever
    /// the host's env names, so its directory rebuilds: compared with the host's CC, which CMake never
    /// used, every rebuild was refused.
    /// </summary>
    [Fact]
    public async Task ACompilerGivenAsACacheVariable_RebuildsTheDirectoryItConfigured_WhateverTheHostNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);

        // The gcc the build starts is the one the directory was configured with, found where a survey
        // found it: off a PATH that holds none, in the directory appended to it.
        var gcc = temp.WriteProgram(ToolchainDirectory, "gcc");
        Directory.CreateDirectory(temp.Combine("empty-path"));
        var request = tracked with
        {
            HostEnvironment = new Dictionary<string, string> { ["CC"] = "clang", ["PATH"] = temp.Combine("empty-path") },
            ProgramDirectories = [temp.Combine(ToolchainDirectory)],
        };
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);
        var config = Config();
        config.Toolchains["gcc"] = new ToolchainConfig { Platforms = [PlatformNames.Linux], CacheVars = { ["CMAKE_C_COMPILER"] = "gcc" } };

        // Built once, and configured as CMake would have configured it then.
        await Service(factory, exitCode: 0).BuildAsync(config, request, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:STRING={gcc.Replace('\\', '/')}\n",
            cancellationToken);

        var result = await Service(factory, exitCode: 0).BuildAsync(config, request, cancellationToken);

        Assert.NotEqual(LegVerdict.Poisoned, result.Verdict.Verdict);
        Assert.Null(result.RebuiltFromClean);
    }

    /// <summary>
    /// A relative program the build recorded is read from the build directory, where the check starts,
    /// never from wherever this process began.
    /// </summary>
    [Fact]
    public async Task ARelativeRecordedNinja_IsReadFromTheBuildDirectory()
    {
        using var temp = new TempDirectory();
        var buildDirectory = temp.Combine("build", "x86_64-gcc-debug");
        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName), string.Empty, TestContext.Current.CancellationToken);

        var runner = new RecordingRunner("app.o: #deps 1, deps mtime 1 (VALID)\n    app.h\n");

        _ = await new NinjaDependencyCheck(runner, new HarnessFactory().FileSystem)
            .CheckAsync(buildDirectory, [], "tools/ninja", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(buildDirectory, "tools", "ninja"), Assert.Single(runner.Started).FileName);
    }

    /// <summary>
    /// A check that could not start says so as a check that did not run - never as the build failing,
    /// and never as a pass.
    /// </summary>
    [Fact]
    public async Task ADependencyCheckThatCouldNotStart_IsACheckThatDidNotRun()
    {
        using var temp = new TempDirectory();
        var buildDirectory = temp.Combine("build", "x86_64-gcc-debug");
        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName), string.Empty, TestContext.Current.CancellationToken);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProgramStartException("/opt/arm/bin/ninja", "'/opt/arm/bin/ninja' could not be started: Text file busy"));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => new NinjaDependencyCheck(runner, new HarnessFactory().FileSystem)
            .CheckAsync(buildDirectory, [], "/opt/arm/bin/ninja", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, failure.ExitCode);
        Assert.Contains("could not be started", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Answers every program with <paramref name="output"/>, recording what it was asked to start.</summary>
    private sealed class RecordingRunner(string output) : IProcessRunner
    {
        public List<ProcessRequest> Started { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Started.Add(request);
            return Task.FromResult(new ProcessResult(0, output, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>A runner that starts nothing, prints nothing, and exits as it was told to.</summary>
    private sealed class QuietRunner(int exitCode) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));

        public string? FindExecutable(string command) => command;
    }

    /// <summary>
    /// Every phase starts nothing. As configure starts, <paramref name="configuring"/> happens, and as
    /// the build starts, <paramref name="building"/> - an input rewritten, as an editor saving mid-build
    /// does; an object compiled; the clock stepped; the run stopped, as its caller's time limit stops one -
    /// and each goes on only if the run still wants it, configure exiting 0 and the build
    /// <paramref name="buildExitCode"/>.
    /// </summary>
    private sealed class ScriptedPhases(Action? configuring = null, Action? building = null, int buildExitCode = 0) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var build = request.Arguments is ["--build", ..];

            (build ? building : configuring)?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new ProcessResult(build ? buildExitCode : 0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>
    /// A build system's leavings: as the build phase starts, <paramref name="files"/> are written below the
    /// directory it builds - what <c>cmake --build</c> names, or a dotnet build's artifacts path - before
    /// <paramref name="inner"/> runs it. Written while the build runs, never before it: a directory holding
    /// files and no record is one nothing says what was built from, and it starts from clean.
    /// </summary>
    private sealed class Leaving(IProcessRunner inner, IReadOnlyList<string> files) : IProcessRunner
    {
        /// <summary>What a dotnet build names the directory it builds in with.</summary>
        private const string ArtifactsPath = "--artifacts-path:";

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var directory = request.Arguments is ["--build", var built, ..]
                ? built
                : request.Arguments.FirstOrDefault(argument => argument.StartsWith(ArtifactsPath, StringComparison.Ordinal))?[ArtifactsPath.Length..];

            foreach (var file in directory is null ? [] : files)
            {
                var path = Path.Combine(directory!, file);

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "built");
            }

            return inner.RunAsync(request, cancellationToken);
        }

        public string? FindExecutable(string command) => inner.FindExecutable(command);
    }

    /// <summary>A file system that refuses a walk of one build directory, as one it may not read refuses.</summary>
    private sealed class UnreadableBuildDirectory(IFileSystem inner, string buildDirectory) : PassThroughFileSystem(inner)
    {
        /// <summary>What the refusal says.</summary>
        public const string Refusal = "Access to the path is denied to this test.";

        /// <summary>How often the build directory was walked.</summary>
        public int Walks { get; private set; }

        public override IEnumerable<WrittenFile> EnumerateWrittenFiles(string path)
        {
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(buildDirectory), StringComparison.Ordinal))
            {
                return base.EnumerateWrittenFiles(path);
            }

            Walks++;

            return Enumerable.Range(0, 1).Select<int, WrittenFile>(_ => throw new UnauthorizedAccessException(Refusal));
        }
    }

    /// <summary>A file system that counts how often one input is opened for its content.</summary>
    private sealed class CountingReads(IFileSystem inner, string input) : PassThroughFileSystem(inner)
    {
        /// <summary>How often the input was opened.</summary>
        public int Reads { get; private set; }

        public override Stream OpenRead(string path)
        {
            if (path.Replace('\\', '/').EndsWith(input, StringComparison.Ordinal))
            {
                Reads++;
            }

            return base.OpenRead(path);
        }
    }

    /// <summary>
    /// A file system that cannot open one input, as one held open by another process cannot be: the
    /// first <paramref name="refusals"/> times it is asked, or every time.
    /// </summary>
    private sealed class UnreadableInput(IFileSystem inner, string input, int refusals = int.MaxValue) : PassThroughFileSystem(inner)
    {
        private int _refused;

        public override Stream OpenRead(string path)
            => path.Replace('\\', '/').EndsWith(input, StringComparison.Ordinal) && _refused++ < refusals
                ? throw new IOException($"'{path}' is held open by another process.")
                : base.OpenRead(path);
    }

    /// <summary>A file system that cannot say when one input was written, as one gone since it was read cannot.</summary>
    private sealed class UndatableInput(IFileSystem inner, string input) : PassThroughFileSystem(inner)
    {
        /// <summary>What the refusal says.</summary>
        public const string Refusal = "The input could not be asked when it was last written.";

        public override DateTime LastWriteTimeUtc(string path)
            => path.Replace('\\', '/').EndsWith(input, StringComparison.Ordinal)
                ? throw new IOException(Refusal)
                : base.LastWriteTimeUtc(path);
    }

    /// <summary>
    /// A compiler that preprocesses the probed line into MSVC's <paramref name="msvc"/> - _MSC_VER,
    /// _MSC_FULL_VER and _MSC_BUILD - defining nothing else, and remembers every request.
    /// </summary>
    private sealed class VersionAnswering(string msvc) : IProcessRunner
    {
        private readonly List<ProcessRequest> _requests = [];

        /// <summary>Every request it was given.</summary>
        public IReadOnlyList<ProcessRequest> Requests => _requests;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);

            return Task.FromResult(new ProcessResult(
                0,
                $"repo_harness_compiler_version {msvc} __GNUC__ __GNUG__ __GNUC_MINOR__ __GNUC_PATCHLEVEL__ __clang_major__ __clang_minor__ __clang_patchlevel__ __apple_build_version__\n",
                "compiler-version-CXX.cpp\n",
                TimeSpan.Zero,
                TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>A process table this machine will not let anybody read.</summary>
    private sealed class UnreadableProcessTable : IProcessTable
    {
        /// <summary>Why every reading comes back empty.</summary>
        public const string Blocked = "the process query is blocked on this machine";

        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessTableReading([], Blocked));
    }
}
