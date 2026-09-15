namespace RepoHarness.Core.Configuration;

/// <summary>
/// The machines legs run on. The machine running the harness is always one of them; a WSL
/// distribution and an ssh host join by being declared here, under the name the command line
/// selects them by: <c>--wsl &lt;distro&gt;</c> and <c>--ssh &lt;name&gt;</c>.
/// </summary>
/// <remarks>
/// Whether a host can be reached is never declared. It is measured before anything starts, so a
/// configuration shared between machines stays true on every one of them.
/// </remarks>
public sealed class HostsConfig
{
    /// <summary>Settings for the machine running the harness.</summary>
    public LocalHostConfig Local { get; init; } = new();

    /// <summary>WSL distributions, keyed by the distribution's name as WSL lists it.</summary>
    public Dictionary<string, WslHostConfig> Wsl { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ssh hosts, keyed by the <c>Host</c> name declared in <c>.harness-config/ssh/config</c>, which is
    /// where the address, the user and the key are found.
    /// </summary>
    public Dictionary<string, SshHostConfig> Ssh { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Settings every kind of host accepts.</summary>
public abstract class HostSettings
{
    /// <summary>
    /// Cores a build uses on this machine, replacing <c>defaults.buildCores</c>. A remote
    /// host rarely has the same core count as the machine the configuration was written on.
    /// </summary>
    public int? BuildCores { get; init; }

    /// <summary>Cores a test run uses on this machine, replacing <c>defaults.testCores</c>.</summary>
    public int? TestCores { get; init; }

    /// <summary>
    /// Command that keeps this machine awake while a leg runs on it, with <c>{pid}</c> replaced
    /// by the process it should outlive, such as <c>["caffeinate", "-dimsu", "-w", "{pid}"]</c> on
    /// macOS. A host that sleeps mid-leg charges the sleep to whatever was running, which once
    /// reported a 4 ms test at 729 s. Without it, the leg's timings are marked suspect.
    /// </summary>
    public List<string>? KeepAwake { get; init; }

    /// <summary>
    /// Compiler cache directory on this machine, set explicitly so that two hosts never share
    /// one store. A shared store lets one host's objects satisfy another host's build, which is
    /// a contamination this prevents by construction.
    /// </summary>
    public string? CompilerCacheDirectory { get; init; }

    /// <summary>
    /// Environment applied to every phase run on this machine. Names compare ignoring case on
    /// every platform, as they do on Windows.
    /// </summary>
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The machine running the harness.</summary>
public sealed class LocalHostConfig : HostSettings
{
}

/// <summary>A host reached through a transport, which keeps a copy of the repository of its own.</summary>
public abstract class RemoteHostConfig : HostSettings
{
    /// <summary>
    /// Where this host keeps its copy of the repository: an absolute path, or one starting with
    /// <c>~/</c> for the home directory of the user the host is reached as.
    /// </summary>
    public required string RepositoryPath { get; init; }
}

/// <summary>A WSL distribution on the Windows machine running the harness.</summary>
public sealed class WslHostConfig : RemoteHostConfig
{
}

/// <summary>A machine reached over ssh.</summary>
public sealed class SshHostConfig : RemoteHostConfig
{
    /// <summary>
    /// Seconds an ssh connection may take to open. With <see cref="KeepAliveSeconds"/>, this is
    /// what stops a dead link from hanging a leg with no output and no end.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 25;

    /// <summary>Seconds between ssh keep-alive probes; an unanswered probe ends the connection.</summary>
    public int KeepAliveSeconds { get; init; } = 30;
}
