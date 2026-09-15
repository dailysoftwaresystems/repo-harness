using Microsoft.Extensions.DependencyInjection;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Commands;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Projects;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Cli;

/// <summary>
/// The composition root. Every dependency is assembled here so that no service
/// constructs its own collaborators and every one of them stays substitutable.
/// </summary>
internal static class HarnessServices
{
    /// <summary>Builds the service provider for one command invocation.</summary>
    internal static ServiceProvider Build(bool verbose)
    {
        var services = new ServiceCollection();

        // The only two registrations that observe the operating system. Everything
        // registered after them depends on these abstractions, never on the platform.
        services.AddSingleton<IHostPlatform, HostPlatform>();
        services.AddSingleton(_ => FilePermissionsFactory.Create());

        services.AddSingleton<IHarnessOutput>(_ => new ConsoleHarnessOutput(verbose));
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IRepositoryLocator, RepositoryLocator>();
        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<IGitIgnoreManager, GitIgnoreManager>();
        services.AddSingleton<IProjectDetector, ProjectDetector>();
        services.AddSingleton<IPathBudget, PathBudget>();
        services.AddSingleton<IHarnessContextLoader, HarnessContextLoader>();
        services.AddSingleton<IWorktreeService, WorktreeService>();
        services.AddSingleton<IAnchorRegistryLocator, AnchorRegistryLocator>();
        services.AddSingleton<IAnchorRegistryLock>(provider => new NamedMutexAnchorRegistryLock(
            provider.GetRequiredService<IHostPlatform>(),
            NamedMutexAnchorRegistryLock.DefaultTimeout));
        services.AddSingleton<IAnchorRegistryService, AnchorRegistryService>();
        services.AddSingleton<IAnchorBalanceService, AnchorBalanceService>();

        services.AddSingleton<VerifyGitService>();
        services.AddSingleton<InitService>();

        return services.BuildServiceProvider();
    }
}
