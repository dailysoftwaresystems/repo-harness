using System.Runtime.InteropServices;
using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Projects;
using RepoHarness.Core.Repository;

namespace RepoHarness.Tests;

/// <summary>
/// Expectations come from <see cref="RuntimeInformation"/>, never from the platform
/// object itself: a test that asks the code under test what to expect cannot fail.
/// </summary>
public sealed class HostPlatformTests
{
    private static readonly PlatformId Running =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? PlatformId.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? PlatformId.MacOs
        : PlatformId.Linux;

    [Fact]
    public void Current_IsTheRunningOperatingSystem()
    {
        Assert.Equal(Running, new HostPlatform().Current);
    }

    [Fact]
    public void PlatformKey_IsTheKeyConfigJsonUses()
    {
        var expected = Running switch
        {
            PlatformId.Windows => "windows",
            PlatformId.MacOs => "macos",
            _ => "linux",
        };

        Assert.Equal(expected, new HostPlatform().PlatformKey);
    }

    [Fact]
    public void MaxPathLength_IsSet_OnlyOnWindows()
    {
        Assert.Equal(Running == PlatformId.Windows ? 260 : null, new HostPlatform().MaxPathLength);
    }

    [Fact]
    public void PathComparison_IgnoresCase_OnlyOnWindows()
    {
        var expected = Running == PlatformId.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        Assert.Equal(expected, new HostPlatform().PathComparison);
    }

    [Fact]
    public void ExecutableName_AppendsExe_OnlyOnWindows()
    {
        Assert.Equal(Running == PlatformId.Windows ? "cmake.exe" : "cmake", new HostPlatform().ExecutableName("cmake"));
    }

    [Fact]
    public void ExecutableName_LeavesAnExplicitExtensionAlone()
    {
        Assert.Equal("tool.bat", new HostPlatform().ExecutableName("tool.bat"));
    }
}

public sealed class HarnessLayoutTests
{
    private static readonly string Main = Path.Combine(TestHost.TemporaryRoot, "layout", "repo");

    private static readonly string Worktree = Path.Combine(Main, ".harness-config", "worktrees", "wt");

    [Fact]
    public void IsWorktree_IsFalse_ForTheMainCheckout_WithOrWithoutATrailingSeparator()
    {
        var platform = Comparing(StringComparison.Ordinal);

        Assert.False(new HarnessLayout(Main, Main).IsWorktree(platform));
        Assert.False(new HarnessLayout(Main + Path.DirectorySeparatorChar, Main).IsWorktree(platform));
    }

    [Fact]
    public void IsWorktree_IsTrue_ForALinkedWorktree()
    {
        Assert.True(new HarnessLayout(Worktree, Main).IsWorktree(Comparing(StringComparison.Ordinal)));
    }

    [Fact]
    public void IsWorktree_ComparesPathsTheWayThePlatformDoes()
    {
        // On Linux two directories differing only in case are two directories.
        var layout = new HarnessLayout(Main.ToUpperInvariant(), Main);

        Assert.False(layout.IsWorktree(Comparing(StringComparison.OrdinalIgnoreCase)));
        Assert.True(layout.IsWorktree(Comparing(StringComparison.Ordinal)));
    }

    [Fact]
    public void ConfigFile_ResolvesAgainstTheTree_BecauseItIsTrackedByGit()
    {
        var layout = new HarnessLayout(Worktree, Main);

        Assert.Equal(Path.Combine(Worktree, ".harness-config", "config.json"), layout.ConfigFile);
    }

    [Fact]
    public void IgnoredState_AlwaysResolvesAgainstTheMainCheckout()
    {
        // A worktree checks out the tracked parts of .harness-config but never the
        // ignored ones, and two runs of the same leg must contend over one lock.
        var layout = new HarnessLayout(Worktree, Main);
        var mainHarness = Path.Combine(Main, ".harness-config");

        Assert.Equal(mainHarness, layout.MainHarnessDirectory);
        Assert.Equal(Path.Combine(mainHarness, "worktrees"), layout.WorktreesDirectory);
        Assert.Equal(Path.Combine(mainHarness, "sshItems"), layout.SshItemsDirectory);
        Assert.Equal(Path.Combine(mainHarness, "wslDistros"), layout.WslDistrosDirectory);
        Assert.Equal(Path.Combine(mainHarness, "runner", ".env"), layout.RunnerEnvDirectory);
        Assert.Equal(Path.Combine(mainHarness, "runner", ".secrets"), layout.RunnerSecretsDirectory);
        Assert.Equal(Path.Combine(mainHarness, "lock.json"), layout.LockFile);
    }

    /// <summary>
    /// A run's records belong to the tree that ran it, so a worktree reads what it judged without leaving
    /// its worktree - while the lock two runs contend over stays in the main checkout.
    /// </summary>
    [Fact]
    public void RunRecords_ResolveAgainstTheTreeThatRan_WhileTheLockStaysInTheMainCheckout()
    {
        var worktree = new HarnessLayout(Worktree, Main);
        var main = new HarnessLayout(Main, Main);

        Assert.Equal(Path.Combine(Worktree, ".harness-config", "runs"), worktree.RunsDirectory);
        Assert.Equal(Path.Combine(Worktree, ".harness-config", "runs", "r1"), worktree.RunDirectory("r1"));
        Assert.Equal(Path.Combine(Main, ".harness-config", "runs"), main.RunsDirectory);
        Assert.Equal(Path.Combine(Main, ".harness-config", "lock.json"), worktree.LockFile);
    }

    [Fact]
    public void ActionFiles_ResolveAgainstTheTreeBeingActedOn_BecauseGitTracksThem()
    {
        // The values an action reads are ignored state and belong to the main checkout; the action
        // files themselves are tracked, so a worktree has its own and a runner acts on the tree it
        // was asked about.
        var layout = new HarnessLayout(Worktree, Main);

        Assert.Equal(
            Path.Combine(Worktree, ".harness-config", "runner", "actions"),
            layout.RunnerActionsDirectory);
    }

    [Fact]
    public void WorktreesRoot_FollowsTheConfiguredRoot_BecauseItSpendsPathBudget()
    {
        // The default root spends 22 characters before a worktree's own name. On Windows that
        // difference decides whether any name fits inside the path limit at all.
        var layout = new HarnessLayout(Worktree, Main);

        PathAssert.Same(
            Path.Combine(Main, ".worktrees"),
            layout.WorktreesDirectoryUnder(".worktrees"));
    }

    [Fact]
    public void WorktreePath_LiesInsideTheWorktreesDirectory()
    {
        var layout = new HarnessLayout(Worktree, Main);

        Assert.True(PathContainment.IsStrictlyInside(
            layout.WorktreesDirectory,
            layout.WorktreePath("other"),
            StringComparison.Ordinal));
    }

    private static IHostPlatform Comparing(StringComparison comparison)
    {
        var platform = Substitute.For<IHostPlatform>();
        platform.PathComparison.Returns(comparison);
        return platform;
    }
}

public sealed class ProjectDetectorTests
{
    [Fact]
    public void Detect_FindsNothing_InAnEmptyDirectory()
    {
        using var temp = new TempDirectory();

        Assert.Empty(CreateDetector().Detect(temp.Path));
    }

    [Fact]
    public void Detect_FindsNothing_WhenTheDirectoryDoesNotExist()
    {
        using var temp = new TempDirectory();

        Assert.Empty(CreateDetector().Detect(temp.Combine("absent")));
    }

    [Fact]
    public void Detect_RecognisesCmake()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("CMakeLists.txt");

        var project = Assert.Single(CreateDetector().Detect(temp.Path));

        Assert.Equal("cmake", project.Type);
        Assert.Equal(".", project.Path);
    }

    [Fact]
    public void Detect_RecognisesDart()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("pubspec.yaml");

        Assert.Equal("dart", Assert.Single(CreateDetector().Detect(temp.Path)).Type);
    }

    [Theory]
    [InlineData("App.sln")]
    [InlineData("App.slnx")]
    [InlineData("Tool.csproj")]
    public void Detect_RecognisesDotnet_ByItsSolutionOrProjectFile(string fileName)
    {
        using var temp = new TempDirectory();
        temp.WriteFile(fileName);

        var project = Assert.Single(CreateDetector().Detect(temp.Path));

        Assert.Equal("dotnet", project.Type);
        Assert.Equal(fileName, project.Path);
    }

    [Fact]
    public void Detect_PrefersASolution_OverAProjectFile()
    {
        // The solution names the whole build; one project file is only part of it.
        using var temp = new TempDirectory();
        temp.WriteFile("A.csproj");
        temp.WriteFile("Z.sln");

        Assert.Equal("Z.sln", Assert.Single(CreateDetector().Detect(temp.Path)).Path);
    }

    [Fact]
    public void Detect_ReportsEveryProjectKind_InPriorityOrder()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("App.sln");
        temp.WriteFile("pubspec.yaml");
        temp.WriteFile("CMakeLists.txt");

        var detected = CreateDetector().Detect(temp.Path);

        Assert.Equal(["cmake", "dart", "dotnet"], detected.Select(project => project.Type));
    }

    private static ProjectDetector CreateDetector()
        => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));
}
