using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>Builds the transport that reaches one host's copy of the repository.</summary>
public interface ISyncTransportFactory
{
    /// <summary>The transport for <paramref name="host"/>.</summary>
    /// <param name="host">The host whose copy is to be written.</param>
    /// <exception cref="HarnessException">
    /// The host was measured as unusable, so nothing can be written there. Raised rather than
    /// returning a transport that would fail on its first call, so the reason names the host.
    /// </exception>
    ISyncTransport For(HostReport host);
}

/// <inheritdoc cref="ISyncTransportFactory"/>
public sealed class SyncTransportFactory(
    IFileSystem fileSystem,
    IManifestBuilder manifestBuilder,
    IGitClient gitClient,
    IHostCommandRunner hostCommandRunner,
    Platform.IHostPlatform platform,
    IHarnessOutput output) : ISyncTransportFactory
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IManifestBuilder _manifestBuilder = manifestBuilder;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IHostCommandRunner _hostCommandRunner = hostCommandRunner;
    private readonly Platform.IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <inheritdoc/>
    public ISyncTransport For(HostReport host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (host.Host.Kind == HostKind.Local)
        {
            return new LocalSyncTransport(_fileSystem, _manifestBuilder, _gitClient, _platform);
        }

        var session = host.Session ?? throw new HarnessException(
            HarnessExit.HostUnavailable,
            $"{host.Host} was not reached, so nothing can be written there: {host.Reason ?? "no reason was recorded"}.");

        return new RemoteSyncTransport(host.Host, session, _hostCommandRunner, _output, host.KeepAwake);
    }
}
