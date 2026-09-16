using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
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

    [Fact]
    public async Task ACitationOfAParent_IsResolvedByAMoreSpecificRow()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await WriteAnchorAsync(harness, temp, Known + "-DETAIL");

        temp.WriteFile(Path.Combine("src", "thing.cpp"), $"// see {Known}\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.True(report.Passed);
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
