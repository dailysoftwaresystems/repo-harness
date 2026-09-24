using NSubstitute;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Commands;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Projects;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Tools;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// Assembles real services for tests that exercise behaviour end to end. Using the
/// real collaborators is deliberate: these services exist to manipulate git and the
/// file system, and a substitute for both would only prove the substitute works.
/// </summary>
public sealed class HarnessFactory
{
    public HarnessFactory(bool verbose = false)
    {
        Platform = new HostPlatform();
        FilePermissions = FilePermissionsFactory.Create();

        // The real console output, writing to buffers: a hand-written recorder would test
        // that the recorder behaves, not that the output does.
        Output = new ConsoleHarnessOutput(StandardOutput, StandardError, verbose);

        FileSystem = new PhysicalFileSystem(FilePermissions);
        ProcessRunner = new ProcessRunner(Platform, FilePermissions);
        ProcessTable = ProcessTableFactory.Create(Platform, ProcessRunner);
        GitClient = new GitClient(ProcessRunner, Output);
        RepositoryLocator = new RepositoryLocator(GitClient);
        ConfigStore = new JsonConfigStore(FileSystem);
        GitIgnoreManager = new GitIgnoreManager(FileSystem);
        ProjectDetector = new ProjectDetector(FileSystem);
        VerifyGitService = new VerifyGitService(GitClient);

        PathBudget = new PathBudget(Platform);
        ContextLoader = new HarnessContextLoader(
            RepositoryLocator, ConfigStore, GitClient, FileSystem, Platform, Output);
        WorktreeService = new WorktreeService(ContextLoader, GitClient, FileSystem, PathBudget, Platform, Output, HostCopies);

        AnchorRegistryLocator = new AnchorRegistryLocator(GitClient);
        AnchorRegistryLock = new NamedMutexAnchorRegistryLock(Platform, NamedMutexAnchorRegistryLock.DefaultTimeout);
        AnchorRegistryService = new AnchorRegistryService(ContextLoader, AnchorRegistryLocator, AnchorRegistryLock, FileSystem);
        AnchorBalanceService = new AnchorBalanceService(ContextLoader, AnchorRegistryLocator, GitClient, FileSystem);

        // A double rather than the real service: init --install-tools calls it for every declared leg,
        // and the real one reaches hosts. A test that declared a leg would otherwise try to install a
        // .NET SDK somewhere, which is not what any of these tests are about.
        ToolProvisionService = Substitute.For<IToolProvisionService>();
        ToolProvisionService
            .ProvisionAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ToolProvisionReport([])));

        InitService = new InitService(
            FileSystem,
            RepositoryLocator,
            ConfigStore,
            GitIgnoreManager,
            ProjectDetector,
            VerifyGitService,
            AnchorRegistryLocator,
            ToolProvisionService,
            GitClient,
            new ManagedIgnoreCheck(GitClient, FileSystem, Platform, Output),
            Platform);
    }

    public StringWriter StandardOutput { get; } = new();

    public StringWriter StandardError { get; } = new();

    public IHostPlatform Platform { get; }

    public IFilePermissions FilePermissions { get; }

    public IHarnessOutput Output { get; }

    public IFileSystem FileSystem { get; }

    public IProcessRunner ProcessRunner { get; }

    /// <summary>How this machine publishes its process table, which contention sampling reads.</summary>
    public IProcessTable ProcessTable { get; }

    public IGitClient GitClient { get; }

    public IRepositoryLocator RepositoryLocator { get; }

    public IConfigStore ConfigStore { get; }

    /// <summary>How a process is told from the next holder of its id; the real one, so tests measure this process.</summary>
    public IProcessIdentity Identity { get; } = new ProcessIdentity(new HostPlatform());

    public IGitIgnoreManager GitIgnoreManager { get; }

    public IProjectDetector ProjectDetector { get; }

    public IPathBudget PathBudget { get; }

    /// <summary>What deleting a worktree does on hosts: nothing, since no host is reached from these tests.</summary>
    public IHostCopyRemover HostCopies { get; } = new NoHostCopies();

    public IHarnessContextLoader ContextLoader { get; }

    public IWorktreeService WorktreeService { get; }

    public IAnchorRegistryLocator AnchorRegistryLocator { get; }

    public NamedMutexAnchorRegistryLock AnchorRegistryLock { get; }

    public IAnchorRegistryService AnchorRegistryService { get; }

    public IAnchorBalanceService AnchorBalanceService { get; }

    public VerifyGitService VerifyGitService { get; }

    public InitService InitService { get; }

    /// <summary>The tool provisioning init calls, a double so no test reaches a host.</summary>
    public IToolProvisionService ToolProvisionService { get; }

    /// <summary>Creates a git repository with one commit, so worktrees can be added.</summary>
    public async Task InitializeGitRepositoryAsync(string path, CancellationToken cancellationToken)
    {
        await RunGitAsync(path, ["init", "--quiet", "."], cancellationToken);
        await RunGitAsync(path, ["config", "user.email", "harness@test.invalid"], cancellationToken);
        await RunGitAsync(path, ["config", "user.name", "Harness Test"], cancellationToken);

        File.WriteAllText(Path.Combine(path, "README.md"), "test repository");

        await RunGitAsync(path, ["add", "-A"], cancellationToken);
        await RunGitAsync(path, ["commit", "--quiet", "-m", "initial"], cancellationToken);
    }

    /// <summary>
    /// Creates a repository, runs init in it, and replaces the seeded configuration with
    /// <paramref name="config"/> when one is given.
    /// </summary>
    public async Task InitializeHarnessAsync(
        string path,
        CancellationToken cancellationToken,
        HarnessConfig? config = null)
    {
        await InitializeGitRepositoryAsync(path, cancellationToken);

        var outcome = await InitService.InitializeAsync(path, cancellationToken);
        Assert.True(outcome.Succeeded, outcome.Message);

        if (config is not null)
        {
            WriteConfig(path, config);
        }
    }

    /// <summary>Commits everything in the working tree.</summary>
    public async Task CommitAllAsync(string path, string message, CancellationToken cancellationToken)
    {
        await RunGitAsync(path, ["add", "-A"], cancellationToken);
        await RunGitAsync(path, ["commit", "--quiet", "--allow-empty", "-m", message], cancellationToken);
    }

    /// <summary>Replaces the configuration of the repository at <paramref name="repositoryRoot"/>.</summary>
    public void WriteConfig(string repositoryRoot, HarnessConfig config)
        => ConfigStore.Save(ConfigPath(repositoryRoot), config);

    /// <summary>Where the configuration of <paramref name="repositoryRoot"/> lives.</summary>
    public static string ConfigPath(string repositoryRoot)
        => Path.Combine(repositoryRoot, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);

    /// <summary>Where a worktree of <paramref name="repositoryRoot"/> lives.</summary>
    public static string WorktreePath(string repositoryRoot, string name)
        => Path.Combine(
            repositoryRoot,
            HarnessLayout.DirectoryName,
            HarnessLayout.WorktreesDirectoryName,
            name);

    /// <summary>Runs git, failing the test when git itself fails.</summary>
    public async Task<GitCommandResult> RunGitAsync(
        string directory,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var result = await GitClient.RunAsync(directory, arguments, cancellationToken: cancellationToken);

        Assert.True(
            result.Succeeded,
            $"git {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.FailureMessage}");

        return result;
    }

    /// <summary>
    /// Deletes the object <paramref name="revision"/> names from the repository's store, as a damaged
    /// repository - or a partial clone that cannot fetch it - lacks one: git still lists the file, and
    /// cannot read it.
    /// </summary>
    public async Task LoseObjectAsync(string repository, string revision, CancellationToken cancellationToken)
    {
        var objectId = (await RunGitAsync(repository, ["rev-parse", revision], cancellationToken)).StandardOutput.Trim();
        var loose = Path.Combine(repository, ".git", "objects", objectId[..2], objectId[2..]);

        // git writes its objects read-only.
        File.SetAttributes(loose, FileAttributes.Normal);
        File.Delete(loose);
    }

    /// <summary>
    /// Stores <paramref name="content"/> and stages it under <paramref name="quotedName"/>, a name as
    /// git's C-style quoting spells it: so a test can hold a name no file system here can, such as
    /// bytes that are not UTF-8 (<c>"caf\351.md"</c>) or a line break.
    /// </summary>
    /// <param name="repository">The repository's root.</param>
    /// <param name="quotedName">The name, in double quotes, with git's escapes.</param>
    /// <param name="content">What the file holds.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    public async Task StageAsync(string repository, string quotedName, string content, CancellationToken cancellationToken)
    {
        var stored = await RunGitWithInputAsync(repository, ["hash-object", "-w", "--stdin"], content, cancellationToken);

        await RunGitWithInputAsync(
            repository,
            ["update-index", "--index-info"],
            $"100644 {stored.Trim()} 0\t{quotedName}\n",
            cancellationToken);
    }

    /// <summary>Runs git with <paramref name="input"/> on its standard input, failing the test when git fails.</summary>
    private async Task<string> RunGitWithInputAsync(
        string directory,
        string[] arguments,
        string input,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            new ProcessRequest { FileName = "git", Arguments = arguments, WorkingDirectory = directory, StandardInput = input },
            cancellationToken);

        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.StandardError}");

        return result.StandardOutput;
    }
}
