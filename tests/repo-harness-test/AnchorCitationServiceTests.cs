using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// check-anchor-citations against a real repository: what is scanned, what resolves, and what the
/// command answers when it was given nothing to scan.
/// </summary>
public sealed class AnchorCitationServiceTests
{
    private const string Known = "D-AREA-TOPIC-ONE";

    [Fact]
    public async Task AnIdCitedAfterAnEscape_IsCheckedLikeAnyOther()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        // The literal the guard this replaces drops, carrying an id that resolves to no row.
        temp.WriteFile(Path.Combine("src", "emit.cpp"), @"    stream << ""\nD-AREA-TOPIC-MISSING: note"";" + "\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        var found = Assert.Single(report.Unresolved);
        Assert.Equal("D-AREA-TOPIC-MISSING", found.Id);
        Assert.Equal("src/emit.cpp", found.Path);
        Assert.Equal(1, found.LineNumber);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task ACitationWithARow_Resolves_AndAWordThatMerelyLooksLikeOneIsNotACitation()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await WriteAnchorAsync(harness, temp, Known);

        temp.WriteFile(
            Path.Combine("src", "thing.cpp"),
            $"// see {Known}\n// a FIXED-32-BIT-WORD value\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(1, report.CitationsFound);
    }

    /// <summary>
    /// A citation resolves to a row whose id is exactly the id cited, by the rule read-anchor finds a
    /// row by. A parent's id is not answered by a more specific row, and a fragment a wrapped line cut
    /// short is not answered by the id it was cut from: resolved by containment, both passed, and
    /// the gate called present a row read-anchor could not find.
    /// </summary>
    [Fact]
    public async Task ACitation_ResolvesOnlyToARowWhoseIdItIs_AsReadAnchorFindsIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await WriteAnchorAsync(harness, temp, Known + "-DETAIL");
        await WriteAnchorAsync(harness, temp, "D-AREA-TOPIC-THIRTY");

        temp.WriteFile(
            Path.Combine("src", "thing.cpp"),
            $"// see {Known}\n// see D-AREA-TOPIC-THIRTY\n// a note that wraps (D-AREA-TOPIC-THIR-\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.Equal([Known, "D-AREA-TOPIC-THIR"], report.Unresolved.Select(citation => citation.Id));

        var lookup = await harness.AnchorRegistryService.ReadAsync(
            temp.Path,
            [Known, "D-AREA-TOPIC-THIRTY", "D-AREA-TOPIC-THIR"],
            AnchorScope.All,
            cancellationToken);

        Assert.Equal(
            report.Unresolved.Select(citation => citation.Id),
            lookup.Missing.Select(result => result.Id));
    }

    /// <summary>
    /// A citation cut at the end of its line is reported whatever rows exist - even when the part
    /// before the cut is itself a row, which it does not mean: the id it was cut from is on the next
    /// line, and would stop resolving without the gate saying so.
    /// </summary>
    [Fact]
    public async Task ACitationCutAtTheEndOfItsLine_IsReported_EvenWhereThePartBeforeTheCutIsARow()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await WriteAnchorAsync(harness, temp, Known);
        await WriteAnchorAsync(harness, temp, Known + "-DETAIL");

        temp.WriteFile(Path.Combine("src", "thing.cpp"), $"// (see {Known}-\n// DETAIL)\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        var cut = Assert.Single(report.Unresolved);
        Assert.Equal(Known, cut.Id);
        Assert.True(cut.Cut);

        // Nor does the headline send the reader to add a row: a cut fails whatever rows exist.
        var outcome = AnchorCitationReports.Render(report, json: false);
        Assert.Equal($"src/thing.cpp:1: {Known}- (cut at the end of the line)", Assert.Single(outcome.Data));
        Assert.StartsWith("1 citation(s) are cut at the end of a line", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("add the row", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The headline says cut citations apart from those no row resolves, each with its own remedy. An
    /// id cut at its second hyphen is found too, where the next line carries it on.
    /// </summary>
    [Fact]
    public async Task TheHeadline_SaysCutCitationsApart_FromThoseNoRowResolves()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "thing.cpp"), "// D-AREA-TOPIC-MISSING, and a wrapped (D-PP-\n// PRESCAN)\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);
        var outcome = AnchorCitationReports.Render(report, json: false);

        Assert.Equal(
            ["src/thing.cpp:1: D-AREA-TOPIC-MISSING", "src/thing.cpp:1: D-PP- (cut at the end of the line)"],
            outcome.Data);
        Assert.StartsWith(
            "1 citation(s) of 1 anchor id(s) resolve to no row in either registry; add the row, or correct the citation. "
            + "1 citation(s) are cut at the end of a line, so none spells the id it was cut from; keep each id whole on one line. "
            + "Scanned ",
            outcome.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Two names that read alike are two files: one holding U+FFFD of its own, and one whose stray
    /// byte reads as U+FFFD. Taken for one, the second - listed after the first - went unread and
    /// unrefused.
    /// </summary>
    [Fact]
    public async Task TwoNamesThatReadAlike_AreTwoFiles()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        await harness.StageAsync(temp.Path, "\"src/caf\\357\\277\\275.cpp\"", "// D-AREA-TOPIC-OWN\n", cancellationToken);
        await harness.StageAsync(temp.Path, "\"src/caf\\377.cpp\"", "// D-AREA-TOPIC-STRAY\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "alike"], cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken));

        Assert.Contains(@"'src/caf\377.cpp' is not named in UTF-8", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file in conflict is scanned once: git lists it once for each side of the conflict, and scanned
    /// that many times, each citation in it was reported that many times.
    /// </summary>
    [Fact]
    public async Task AFileInConflict_IsScannedOnce()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        var thing = Path.Combine("src", "thing.cpp");

        temp.WriteFile(thing, "// D-AREA-TOPIC-MISSING\nbase\n");
        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "side"], cancellationToken);
        temp.WriteFile(thing, "// D-AREA-TOPIC-MISSING\nside\n");
        await harness.CommitAllAsync(temp.Path, "side", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-"], cancellationToken);
        temp.WriteFile(thing, "// D-AREA-TOPIC-MISSING\nmain\n");
        await harness.CommitAllAsync(temp.Path, "main", cancellationToken);

        var merge = await harness.GitClient.RunAsync(temp.Path, ["merge", "--quiet", "side"], cancellationToken: cancellationToken);
        Assert.False(merge.Succeeded, "the merge was to leave src/thing.cpp in conflict");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(["D-AREA-TOPIC-MISSING"], report.Unresolved.Select(citation => citation.Id));
    }

    /// <summary>
    /// A file in a root whose name is not UTF-8 refuses the check, naming it, however the files are
    /// read: no file opens by such a name here, and read as UTF-8 it became another name - one the
    /// commit "could not read", sending the reader to git fsck, and one the disk silently did not
    /// have. Outside every root it is no concern of the check - 'src' then a stray byte included,
    /// which a quoted escape's backslash put inside the root 'src'.
    /// </summary>
    [Fact]
    public async Task ANameThatIsNotUtf8_InARoot_RefusesTheCheck_HoweverTheFilesAreRead()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        await harness.StageAsync(temp.Path, "\"notes/caf\\351.md\"", "// D-AREA-TOPIC-ELSEWHERE\n", cancellationToken);
        await harness.StageAsync(temp.Path, "\"src\\351.bak\"", "// D-AREA-TOPIC-BESIDE\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "outside"], cancellationToken);

        Assert.True((await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken)).Passed);
        Assert.True((await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken)).Passed);

        await harness.StageAsync(temp.Path, "\"src/caf\\351.cpp\"", "// D-AREA-TOPIC-INSIDE\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "inside"], cancellationToken);

        foreach (var subject in new[] { AnchorCitationSubject.CurrentCommit, AnchorCitationSubject.CurrentTree })
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(
                () => Service(harness).CheckAsync(temp.Path, subject, cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains(@"'src/caf\351.cpp' is not named in UTF-8", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A file the commit lists that git cannot read refuses the check, naming it: skipped, a file
    /// whose object was gone passed a check that read nothing in it.
    /// </summary>
    [Fact]
    public async Task AFileTheCommitListsButGitCannotRead_RefusesTheCheck()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "lost.cpp"), "// D-AREA-TOPIC-LOST\n");
        await harness.CommitAllAsync(temp.Path, "src", cancellationToken);
        await harness.LoseObjectAsync(temp.Path, "HEAD:src/lost.cpp", cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken));

        Assert.Contains("'src/lost.cpp'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git fsck", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A submodule's entry names a commit in another repository, which is no file here: it is not
    /// read, and not mistaken for a file git could not read.
    /// </summary>
    [Fact]
    public async Task ASubmodulesEntry_IsNotReadAsAFile()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "thing.cpp"), "// D-AREA-TOPIC-THING\n");
        await harness.CommitAllAsync(temp.Path, "src", cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.GitClient.RunAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"160000,{head},src/sub"], cancellationToken: cancellationToken);
        await harness.GitClient.RunAsync(temp.Path, ["commit", "-q", "-m", "submodule"], cancellationToken: cancellationToken);

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken);

        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(["D-AREA-TOPIC-THING"], report.Unresolved.Select(citation => citation.Id));
    }

    /// <summary>
    /// A commit's files are read by one git process however many there are. Asked for one at a time,
    /// each cost two, and 2,385 files took twenty minutes where reading the disk took seconds.
    /// </summary>
    [Fact]
    public async Task ACommitsFiles_AreReadByOneGitProcess()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        for (var index = 0; index < 5; index++)
        {
            temp.WriteFile(Path.Combine("src", $"file{index}.cpp"), $"// D-AREA-TOPIC-FILE{index}\n");
        }

        await harness.CommitAllAsync(temp.Path, "src", cancellationToken);

        // Changed on disk since: the commit's own text is what a commit's check reads.
        temp.WriteFile(Path.Combine("src", "file0.cpp"), "// D-AREA-TOPIC-CHANGED\n");

        var processes = new CountingProcesses(harness.ProcessRunner);
        var service = new AnchorCitationService(
            harness.ContextLoader,
            harness.AnchorRegistryService,
            new GitClient(processes, harness.Output),
            harness.FileSystem);

        var report = await service.CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken);

        Assert.Equal(5, report.FilesScanned);
        Assert.Equal(
            ["D-AREA-TOPIC-FILE0", "D-AREA-TOPIC-FILE1", "D-AREA-TOPIC-FILE2", "D-AREA-TOPIC-FILE3", "D-AREA-TOPIC-FILE4"],
            report.Unresolved.Select(citation => citation.Id).Order(StringComparer.Ordinal));
        Assert.Single(processes.Started, request => request.Arguments[0] == "cat-file");
        Assert.DoesNotContain(processes.Started, request => request.Arguments[0] == "show");
    }

    [Fact]
    public async Task ACitationOutsideEveryDeclaredRoot_IsNotScanned()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("notes", "scratch.md"), "// D-AREA-TOPIC-ELSEWHERE\n");
        temp.WriteFile("srcsibling.txt", "// D-AREA-TOPIC-SIBLING\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(0, report.CitationsFound);
        Assert.Equal(0, report.FilesScanned);
    }

    [Fact]
    public async Task NoDeclaredRoot_IsRefused_RatherThanReportedAsAPass()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, []);

        temp.WriteFile(Path.Combine("src", "thing.cpp"), "// D-AREA-TOPIC-MISSING\n");

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("anchors.citationRoots", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCommit_AndTheTree_AnswerAboutDifferentFiles()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "committed.cpp"), "// D-AREA-TOPIC-COMMITTED\n");
        await harness.CommitAllAsync(temp.Path, "src", cancellationToken);

        temp.WriteFile(Path.Combine("src", "pending.cpp"), "// D-AREA-TOPIC-PENDING\n");

        var commit = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken);
        var tree = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.Equal(["D-AREA-TOPIC-COMMITTED"], commit.Unresolved.Select(citation => citation.Id));

        Assert.Equal(
            ["D-AREA-TOPIC-COMMITTED", "D-AREA-TOPIC-PENDING"],
            tree.Unresolved.Select(citation => citation.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheReport_NamesEveryUnresolvedCitation_AndFailsWithTheAnchorCode()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "thing.cpp"), "// D-AREA-TOPIC-MISSING\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);
        var outcome = AnchorCitationReports.Render(report, json: false);

        Assert.Equal(AnchorExit.Findings, outcome.ExitCode);
        Assert.Equal("src/thing.cpp:1: D-AREA-TOPIC-MISSING", Assert.Single(outcome.Data));
    }

    private static AnchorCitationService Service(HarnessFactory harness)
        => new(harness.ContextLoader, harness.AnchorRegistryService, harness.GitClient, harness.FileSystem);

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, string[] roots)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Anchors = new AnchorSettings { CitationRoots = [.. roots] } });

        return harness;
    }

    private static Task WriteAnchorAsync(HarnessFactory harness, TempDirectory temp, string id)
        => harness.AnchorRegistryService.WriteAsync(
            temp.Path,
            new AnchorWriteRequest(id, "P1", "trigger"),
            dryRun: false,
            TestContext.Current.CancellationToken);
}
