using Microsoft.Extensions.DependencyInjection;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Build;
using RepoHarness.Core.Ci;
using RepoHarness.Core.Commands;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.LineEndings;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Projects;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Runners;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Secrets;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Testing;
using RepoHarness.Core.Tools;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Cli;

/// <summary>
/// The composition root. Every dependency is assembled here so that no service
/// constructs its own collaborators and every one of them stays substitutable.
/// </summary>
internal static class HarnessServices
{
    /// <summary>Builds the service provider for one command invocation.</summary>
    /// <param name="verbose">Whether detail and child process output are shown.</param>
    /// <param name="prompting">
    /// Whether a password may be asked for at the terminal. False under <c>--no-prompt</c>, and for
    /// a process serving another machine, which has nobody to ask.
    /// </param>
    /// <param name="servesAnotherMachine">
    /// Whether the command was asked for by the DssHarness on another machine, through this one's host
    /// agent, rather than typed here.
    /// </param>
    internal static ServiceProvider Build(bool verbose, bool prompting, bool servesAnotherMachine = false)
    {
        var services = new ServiceCollection();

        services.AddSingleton(new CommandOrigin(servesAnotherMachine));

        // The only registrations that observe the operating system: which system this is, how it
        // expresses a file's permissions, and how it publishes its process table. Everything
        // registered after them depends on these abstractions, never on the platform.
        services.AddSingleton<IHostPlatform, HostPlatform>();
        services.AddSingleton(_ => FilePermissionsFactory.Create());
        services.AddSingleton(provider => ProcessTableFactory.Create(
            provider.GetRequiredService<IHostPlatform>(),
            provider.GetRequiredService<IProcessRunner>()));

        services.AddSingleton<IProcessIdentity, ProcessIdentity>();
        services.AddSingleton<IHarnessOutput>(_ => new ConsoleHarnessOutput(verbose));
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IRepositoryLocator, RepositoryLocator>();
        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<IGitIgnoreManager, GitIgnoreManager>();
        services.AddSingleton<ManagedIgnoreCheck>();
        services.AddSingleton<IProjectDetector, ProjectDetector>();
        services.AddSingleton<IPathBudget, PathBudget>();
        services.AddSingleton<IPublishedToolVersions, NuGetPublishedToolVersions>();
        services.AddSingleton<SyncedCopyToolCheck>();
        services.AddSingleton<IHarnessContextLoader, HarnessContextLoader>();
        services.AddSingleton<IWorktreeService, WorktreeService>();
        services.AddSingleton<IAnchorRegistryLocator, AnchorRegistryLocator>();
        services.AddSingleton<IAnchorRegistryLock>(provider => new NamedMutexAnchorRegistryLock(
            provider.GetRequiredService<IHostPlatform>(),
            NamedMutexAnchorRegistryLock.DefaultTimeout));
        services.AddSingleton<IAnchorRegistryService, AnchorRegistryService>();
        services.AddSingleton<IAnchorBalanceService, AnchorBalanceService>();

        services.AddSingleton<IToolIdentityProvider, EntryAssemblyToolIdentityProvider>();
        services.AddSingleton<IHostCommandRunner, HostCommandRunner>();
        services.AddSingleton<EmulatorProbe>();
        services.AddSingleton<HostAgentService>();

        // Reaching a host: its own directory under .harness-config says where it is, a name is
        // resolved with a retry before ssh is started, and where each program is on it is measured
        // per connection rather than assumed from a login shell's PATH.
        services.AddSingleton<IHostSecretsStore, HostSecretsStore>();
        services.AddSingleton<INameLookup, DnsNameLookup>();
        services.AddSingleton<IHostAddressResolver>(provider => new HostAddressResolver(
            provider.GetRequiredService<INameLookup>(),
            TimeProvider.System,
            HostAddressResolver.DefaultRetryDelay));
        services.AddSingleton(provider => new HoldAwakeStore(provider.GetRequiredService<IFileSystem>(), HoldAwakeStore.DefaultPath));
        services.AddSingleton<IDetachedProcessLauncher, DetachedProcessLauncher>();
        services.AddSingleton<HoldAwakeRegistry>();
        services.AddSingleton<SyncedCopyRefusals>();
        services.AddSingleton<CommandEnd>();
        services.AddSingleton(provider => new HoldAwakeService(
            provider.GetRequiredService<HoldAwakeStore>(),
            provider.GetRequiredService<KeepAwake>(),
            TimeProvider.System,
            HoldAwakeService.PollInterval));
        services.AddSingleton(provider => new SshWakeWindow(
            provider.GetRequiredService<INameLookup>(),
            TimeProvider.System,
            SshWakeWindow.DefaultPollDelay));
        services.AddSingleton<LocalProgramResolver>();
        services.AddSingleton<IHostProgramResolver, HostProgramResolver>();
        services.AddSingleton<IHostConnector, HostConnector>();

        services.AddSingleton<IHostInspector, HostInspector>();

        // Reading a terminal without echoing what is typed is the one thing the core cannot do for
        // itself, so it is supplied here, exactly as the console writer above is. Whether anybody is
        // there to ask is measured by the implementation; this only carries the refusal to ask.
        services.AddSingleton<ISuperuserPrompt>(provider => new ConsoleSuperuserPrompt(
            provider.GetRequiredService<IHarnessOutput>(),
            prompting));

        services.AddSingleton<IToolProvisionService, ToolProvisionService>();
        services.AddSingleton<LegsService>();
        services.AddSingleton<HostExecService>();

        // Leg machinery. Shared by build, test and run, so the isolation rules cannot hold for one
        // command and not another.
        services.AddSingleton<PhaseRunner>();
        services.AddSingleton<LegExecutor>();
        services.AddSingleton<RunLock>();
        services.AddSingleton<LogOwnership>();
        services.AddSingleton<InputFingerprint>();
        services.AddSingleton<ProcessSampler>();
        services.AddSingleton<RemoteLegRunner>();
        services.AddSingleton<KeepAwake>();
        services.AddSingleton<LegRunService>();
        services.AddSingleton<CleanService>();

        services.AddSingleton<BuildDirectoryGuard>();
        services.AddSingleton<CMakeToolchainReader>();
        services.AddSingleton<CompilerVersionProbe>();
        services.AddSingleton<DeveloperEnvironmentProbe>();
        services.AddSingleton<DeveloperEnvironmentProvider>();
        services.AddSingleton<NinjaDependencyCheck>();
        services.AddSingleton<NinjaDeadOutputCheck>();
        services.AddSingleton<IBuildService, BuildService>();

        // Sync. The local transport is registered as the interface because it is also what a host
        // runs on its own side, where `sync-serve` resolves exactly this one.
        services.AddSingleton<IManifestBuilder, ManifestBuilder>();
        services.AddSingleton<ISyncTransport, LocalSyncTransport>();
        services.AddSingleton<ISyncTransportFactory, SyncTransportFactory>();
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<IHostCopyRemover, HostCopyRemover>();

        // Predefined runners and their action files.
        services.AddSingleton<IActionFileParser, ActionFileParser>();
        services.AddSingleton<ActionToolPolicy>();
        services.AddSingleton<ActionValuesReader>();
        services.AddSingleton<ExpectedExceptionMatcher>();
        services.AddSingleton<RunCheckGate>();
        services.AddSingleton<RunSegments>();
        services.AddSingleton<IPredefinedActionRunner, PredefinedActionRunner>();
        services.AddSingleton<IRunnerRunService, RunnerRunService>();
        services.AddSingleton<ITestService, TestService>();

        // Guards that became commands.
        services.AddSingleton<IRootLitterService, RootLitterService>();
        services.AddSingleton<IAnchorCitationService, AnchorCitationService>();
        services.AddSingleton<ILineEndingService, LineEndingService>();
        services.AddSingleton<ICiJobSource, GhCiJobSource>();
        services.AddSingleton<ICiLegsService, CiLegsService>();

        services.AddSingleton<VerifyGitService>();
        services.AddSingleton<InitService>();

        return services.BuildServiceProvider();
    }
}
