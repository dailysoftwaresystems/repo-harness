using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

public sealed class RepositoryLocatorTests
{
    private static readonly string Main = Path.Combine(TestHost.TemporaryRoot, "locator", "repo");

    private static readonly string Worktree = Path.Combine(Main, ".harness-config", "worktrees", "wt");

    private readonly IGitClient _git = Substitute.For<IGitClient>();

    [Fact]
    public async Task LocateAsync_ReturnsNull_OutsideARepository()
    {
        _git.GetRepositoryRootAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        Assert.Null(await new RepositoryLocator(_git).LocateAsync(Main, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LocateAsync_PairsTheTreeWithItsMainCheckout()
    {
        _git.GetRepositoryRootAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(Worktree));
        _git.GetMainWorktreeAsync(Worktree, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GitWorktree?>(new GitWorktree(Main, "abc", "main", IsMain: true, IsBare: false)));

        var layout = await new RepositoryLocator(_git).LocateAsync(Worktree, TestContext.Current.CancellationToken);

        Assert.NotNull(layout);
        Assert.Equal(Worktree, layout.RepositoryRoot);
        Assert.Equal(Main, layout.MainCheckoutRoot);
    }

    [Fact]
    public async Task LocateAsync_NeverFallsBackToTheTree_WhenTheMainCheckoutIsUnknown()
    {
        // Inside a worktree that guess is wrong, and worktrees, the run lock and ssh
        // secrets would all resolve inside the worktree.
        _git.GetRepositoryRootAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(Worktree));
        _git.GetMainWorktreeAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<GitWorktree?>(null));

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            new RepositoryLocator(_git).LocateAsync(Worktree, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
    }

    [Fact]
    public async Task LocateAsync_RefusesAWorktreeOfABareRepository()
    {
        // A bare repository has no checkout to keep harness state in.
        _git.GetRepositoryRootAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(Worktree));
        _git.GetMainWorktreeAsync(Worktree, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GitWorktree?>(new GitWorktree(Main, null, null, IsMain: true, IsBare: true)));

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            new RepositoryLocator(_git).LocateAsync(Worktree, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("bare", exception.Message, StringComparison.Ordinal);
    }
}

public sealed class HarnessContextLoaderTests
{
    private static readonly string Main = Path.Combine(TestHost.TemporaryRoot, "loader", "repo");

    private static readonly string Worktree = Path.Combine(Main, ".harness-config", "worktrees", "wt");

    private readonly IRepositoryLocator _locator = Substitute.For<IRepositoryLocator>();
    private readonly IConfigStore _configStore = Substitute.For<IConfigStore>();
    private readonly IGitClient _git = Substitute.For<IGitClient>();
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();

    public HarnessContextLoaderTests()
    {
        _git.IsInstalled().Returns(true);
    }

    [Fact]
    public async Task LoadAsync_ReportsMissingGit_AsAMissingTool()
    {
        _git.IsInstalled().Returns(false);

        var exception = await Assert.ThrowsAsync<HarnessException>(() => Load(Main));

        Assert.Equal(HarnessExit.ToolMissing, exception.ExitCode);
        _ = _locator.DidNotReceiveWithAnyArgs().LocateAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LoadAsync_RefusesADirectoryOutsideARepository()
    {
        _locator.LocateAsync(Main, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(null));

        var exception = await Assert.ThrowsAsync<HarnessException>(() => Load(Main));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
    }

    [Fact]
    public async Task LoadAsync_ReportsNotInitialised_WhenTheMainCheckoutHasNoConfiguration()
    {
        _locator.LocateAsync(Main, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<HarnessLayout?>(new HarnessLayout(Main, Main)));
        _fileSystem.FileExists(Arg.Any<string>()).Returns(false);

        var exception = await Assert.ThrowsAsync<HarnessException>(() => Load(Main));

        Assert.Equal(HarnessExit.NotInitialized, exception.ExitCode);
        _configStore.DidNotReceiveWithAnyArgs().Load(default!);
    }

    [Fact]
    public async Task LoadAsync_ReadsTheMainCheckoutsConfiguration_EvenFromInsideAWorktree()
    {
        // A worktree carries its own checked-out copy of config.json, which is not the one
        // the harness maintains.
        var layout = new HarnessLayout(Worktree, Main);
        var mainConfig = Path.Combine(Main, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        var config = new HarnessConfig();

        _locator.LocateAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(layout));
        _fileSystem.FileExists(mainConfig).Returns(true);
        _configStore.Load(mainConfig).Returns(config);

        var context = await Load(Worktree);

        Assert.Same(layout, context.Layout);
        Assert.Same(config, context.Config);
        _configStore.Received(1).Load(mainConfig);
    }

    private Task<HarnessContext> Load(string directory)
        => new HarnessContextLoader(_locator, _configStore, _git, _fileSystem)
            .LoadAsync(directory, TestContext.Current.CancellationToken);
}
