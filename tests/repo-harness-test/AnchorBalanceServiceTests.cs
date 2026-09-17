using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>The balance against real commits: every base is a commit the test made.</summary>
public sealed class AnchorBalanceServiceTests
{
    private const string One = "D-AREA-TOPIC-ONE";
    private const string Two = "D-AREA-TOPIC-TWO";

    [Fact]
    public async Task AChangeThatClosesAsMuchAsItOpens_Holds()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);

        await harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal((1, 1, 0), (report.OpenAtBase, report.OpenNow, report.NetNew));
        Assert.Equal([One], report.Closed);
        Assert.Equal([Two], report.Opened.Select(opening => opening.Id));
        Assert.Empty(report.Findings);
    }

    /// <summary>
    /// Whether a row is closed was decided by what its Status cell OPENS with, so a cell nothing
    /// can parse still landed in one column and was counted from there. Measured on a consumer's
    /// registry: 'read-anchors --lint' exited 1 on three rows while this verb answered that the
    /// balance held. Both verbs cannot be right about one file, and the one that COUNTS is the one
    /// that must not guess.
    /// </summary>
    [Fact]
    public async Task AStatusCellNothingCanParse_IsRefusedHereToo_NotSilentlyCounted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);

        // Opens with the closed mark, so every reader testing the opening glyph takes it for
        // closed, and none of them reads the words after it.
        var registry = PendingPath(temp);
        var text = await File.ReadAllTextAsync(registry, cancellationToken);

        await File.WriteAllTextAsync(
            registry,
            text.Replace(
                AnchorStatus.Render(AnchorState.Open),
                AnchorStatus.ClosedMark + " CLOSED (superseded)",
                StringComparison.Ordinal),
            cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Contains(
            report.Findings,
            finding => finding.Message.Contains("is not one of", StringComparison.Ordinal));

        // The severity is what decides the verb's answer, and a Warning here would leave this verb
        // saying the balance holds while --lint exits 1 on the same file. That divergence is the
        // whole reason this test exists.
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task AChangeThatLeavesMoreOpenAnchors_Fails()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp);

        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(1, report.NetNew);
        Assert.Equal("trigger for " + One, Assert.Single(report.Opened).Excerpt);
    }

    [Fact]
    public async Task ANewlyDisclosedAnchor_IsNotCounted()
    {
        // Disclosure records debt that already existed; writing it down is not creating it.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp);

        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One, "disclosed"), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal((1, 0), (report.Disclosed, report.NetNew));
        Assert.True(Assert.Single(report.Opened).Disclosed);
    }

    [Fact]
    public async Task AnchorsAreCountedById_AcrossBothRegistries()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);

        // Moved by hand, still open: the count cannot change, and the misfiling is its own finding.
        var pending = PendingPath(temp);
        var row = File.ReadAllLines(pending).Single(line => line.Contains(One, StringComparison.Ordinal));
        File.WriteAllText(pending, File.ReadAllText(pending).Replace(row + "\n", string.Empty, StringComparison.Ordinal));
        File.AppendAllText(DonePath(temp), row + "\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal((1, 1, 0), (report.OpenAtBase, report.OpenNow, report.NetNew));
        Assert.Empty(report.Opened);
        Assert.Empty(report.Closed);
        Assert.False(report.Passed);
        Assert.Contains(report.Findings, finding => finding.Message.Contains("live anchor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClosedAnchorLeftInPending_FailsTheCheck()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | ✅ CLOSED | t | - | - |\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(0, report.NetNew);
        Assert.Contains(report.Findings, finding => finding.Message.Contains("closed anchor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARegistryThatDidNotExistAtTheBase_CountsAsEmpty_AndIsNoted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.InitService.InitializeAsync(temp.Path, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One, "disclosed"), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([AnchorSettings.DefaultPendingAnchorsPath, AnchorSettings.DefaultDoneAnchorsPath], report.MissingAtBase);
        Assert.Equal(0, report.OpenAtBase);
        Assert.True(report.Passed);
    }

    [Fact]
    public async Task HeadGivenExplicitly_BehavesExactlyLikeNoBase()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        var implicitBase = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);
        var explicitBase = await harness.AnchorBalanceService.CheckAsync(temp.Path, "HEAD", cancellationToken);

        Assert.Equal("HEAD", implicitBase.Base);
        Assert.Equal(implicitBase.Commit, explicitBase.Commit);
        Assert.Equal(
            (implicitBase.OpenAtBase, implicitBase.OpenNow, implicitBase.NetNew, implicitBase.Passed),
            (explicitBase.OpenAtBase, explicitBase.OpenNow, explicitBase.NetNew, explicitBase.Passed));
        Assert.Equal(implicitBase.Closed, explicitBase.Closed);
        Assert.Equal(implicitBase.Opened, explicitBase.Opened);
        Assert.Equal(implicitBase.MissingAtBase, explicitBase.MissingAtBase);
        Assert.Equal(implicitBase.Findings, explicitBase.Findings);
    }

    [Fact]
    public async Task AnOlderBase_IsComparedWithTheTreeAsItIsNow()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        await harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "close one", cancellationToken);

        var sinceLast = await harness.AnchorBalanceService.CheckAsync(temp.Path, "HEAD", cancellationToken);
        var sinceBefore = await harness.AnchorBalanceService.CheckAsync(temp.Path, "HEAD~1", cancellationToken);

        Assert.Empty(sinceLast.Closed);
        Assert.Equal([One], sinceBefore.Closed);
    }

    [Fact]
    public async Task ABaseThatNamesNoCommit_IsRefused()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp);

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorBalanceService.CheckAsync(temp.Path, "no-such-branch", TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
    }

    [Fact]
    public async Task ARegistryGitIgnores_IsRefused_BecauseItHasNoHistory()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(".gitignore", "local/\n");
        harness.WriteConfig(temp.Path, new HarnessConfig
        {
            Anchors = new AnchorSettings { PendingAnchorsPath = "local/pending.md", DoneAnchorsPath = "local/done.md" },
        });
        await harness.InitService.InitializeAsync(temp.Path, cancellationToken);

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("ignored by git", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedOrMissingRegistry_FailsTheCheck()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp);
        File.AppendAllText(PendingPath(temp), $"\nA paragraph.\n\n| `{One}` | P1 | 🟠 OPEN | stray | - | - |\n");
        File.Delete(DonePath(temp));

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings, finding => finding.Message.Contains("outside any table", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding => finding.Message.Contains("no done registry", StringComparison.Ordinal));
    }

    private static async Task<HarnessFactory> PrepareCommittedAsync(TempDirectory temp, params string[] openAnchors)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, cancellationToken);

        foreach (var id in openAnchors)
        {
            await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(id), dryRun: false, cancellationToken);
        }

        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);
        return harness;
    }

    private static AnchorWriteRequest Anchor(string id, string status = "open")
        => new(id, "P1", $"trigger for {id}") { Status = status };

    private static string PendingPath(TempDirectory temp) => temp.Combine(".plans", "_deferred-anchor-registry.md");

    private static string DonePath(TempDirectory temp) => temp.Combine(".plans", "_deferred-anchor-registry-done.md");
}
