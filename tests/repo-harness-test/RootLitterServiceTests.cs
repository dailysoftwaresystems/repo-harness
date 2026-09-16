using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>What the root of a checkout may hold, and what a failed look at it means.</summary>
public sealed class RootLitterServiceTests
{
    [Fact]
    public async Task ACleanRoot_ReportsNothing()
    {
        using var temp = new TempDirectory();
        var (harness, service) = await PrepareAsync(temp);

        var report = await service.CheckAsync(temp.Path, TestContext.Current.CancellationToken);

        Assert.True(report.IsClean, string.Join(", ", report.All));
        Assert.Equal(harness.FileSystem.ResolveLinks(temp.Path), harness.FileSystem.ResolveLinks(report.Root));
    }

    [Fact]
    public async Task AnUntrackedFileAtTheRoot_IsLitter()
    {
        using var temp = new TempDirectory();
        var (_, service) = await PrepareAsync(temp);
        var cancellationToken = TestContext.Current.CancellationToken;

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "probe.out"), "x\n", cancellationToken);

        var report = await service.CheckAsync(temp.Path, cancellationToken);

        Assert.Equal("probe.out", Assert.Single(report.Untracked));
    }

    [Fact]
    public async Task AnIgnoredFileAtTheRoot_IsStillLitter()
    {
        // Ignored junk is still junk. A rule written for build output also hides every probe
        // artefact sharing its extension, which is how seven of them once sat at a root a guard
        // called clean.
        using var temp = new TempDirectory();
        var (harness, service) = await PrepareAsync(temp);
        var cancellationToken = TestContext.Current.CancellationToken;

        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".gitignore"), "*.obj\n", cancellationToken);
        await harness.CommitAllAsync(temp.Path, "ignore objects", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "probe.obj"), "x\n", cancellationToken);

        var report = await service.CheckAsync(temp.Path, cancellationToken);

        Assert.Contains("probe.obj", report.Untracked);
    }

    [Fact]
    public async Task AFileBelowTheRoot_IsNotLitter()
    {
        using var temp = new TempDirectory();
        var (_, service) = await PrepareAsync(temp);
        var cancellationToken = TestContext.Current.CancellationToken;

        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "scratch.txt"), "x\n", cancellationToken);

        var report = await service.CheckAsync(temp.Path, cancellationToken);

        Assert.True(report.IsClean, string.Join(", ", report.All));
    }

    [Fact]
    public async Task AnUntrackedDirectory_IsNotLitter()
    {
        // Directories are exempt by construction: an ignored tree such as a build directory is
        // legitimate, and git collapses it to one entry anyway.
        using var temp = new TempDirectory();
        var (_, service) = await PrepareAsync(temp);
        var cancellationToken = TestContext.Current.CancellationToken;

        Directory.CreateDirectory(Path.Combine(temp.Path, "scratch"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "scratch", "x.txt"), "x\n", cancellationToken);

        var report = await service.CheckAsync(temp.Path, cancellationToken);

        Assert.True(report.IsClean, string.Join(", ", report.All));
    }

    [Fact]
    public async Task AnEntryNamedAfterAForeignPath_IsReportedEvenThoughGitIgnoresIt()
    {
        // The one such directory ever seen was created by a tool git had been told to ignore, so
        // asking git would have found nothing. A colon cannot occur in a name on Windows, so the
        // backslash form is the one that can be built here.
        using var temp = new TempDirectory();
        var (harness, service) = await PrepareAsync(temp);
        var cancellationToken = TestContext.Current.CancellationToken;

        var malformed = "leaked\\path";
        var created = Path.Combine(temp.Path, malformed);

        try
        {
            Directory.CreateDirectory(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Assert.Skip($"This filesystem does not allow a directory named '{malformed}': {ex.Message}");
        }

        Assert.SkipUnless(
            Directory.Exists(created) && !Directory.Exists(Path.Combine(temp.Path, "leaked")),
            "This filesystem read the backslash as a separator, so the name under test cannot exist here.");

        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".gitignore"), "leaked*\n", cancellationToken);
        await harness.CommitAllAsync(temp.Path, "ignore it", cancellationToken);

        var report = await service.CheckAsync(temp.Path, cancellationToken);

        Assert.Contains(malformed, report.Malformed);
    }

    [Fact]
    public async Task AStatusThatCouldNotBeRead_IsARefusal_NotAnEmptyList()
    {
        // git exiting non-zero on a damaged index was measured reading as "no untracked files" over
        // a genuinely dirty root.
        using var temp = new TempDirectory();
        var (_, service) = await PrepareAsync(temp);
        var cancellationToken = TestContext.Current.CancellationToken;

        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".git", "index"), "not an index", cancellationToken);

        var failure = await Assert.ThrowsAsync<HarnessException>(
            () => service.CheckAsync(temp.Path, cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, failure.ExitCode);
        Assert.Contains("is unknown", failure.Message, StringComparison.Ordinal);
    }

    private static async Task<(HarnessFactory Harness, IRootLitterService Service)> PrepareAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig());

        await harness.CommitAllAsync(temp.Path, "initialise the harness", TestContext.Current.CancellationToken);

        return (harness, new RootLitterService(harness.ContextLoader, harness.GitClient, harness.FileSystem));
    }
}
