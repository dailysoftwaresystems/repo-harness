namespace RepoHarness.Core.Secrets;

/// <summary>
/// A superuser credential read from the secrets layout. It is carried in this wrapper rather than as
/// a string so that nothing can print it by accident: a record's generated printout, an interpolated
/// message and a debugger all go through <see cref="ToString"/>, and an installer that reported its
/// own command would otherwise publish the credential to a log nobody meant to hold one.
/// </summary>
/// <param name="value">The credential itself.</param>
public sealed class HostCredential(string value)
{
    /// <summary>
    /// The credential, read only at the one place it is written to a privileged command's standard
    /// input. Every other use is a defect.
    /// </summary>
    public string Reveal() => value;

    /// <summary>What a log, a message, a printout of a record holding one, and a debugger see.</summary>
    public override string ToString() => "(hidden)";
}

/// <summary>
/// What one ssh host's directory under <c>.harness-config/sshItems</c> declares: where the machine is,
/// who to be there, and the two files that prove each end of the connection to the other. None of it
/// is in <c>config.json</c>, which git tracks: a repository that named an address, a user or a key
/// path would publish the reach of everybody who cloned it.
/// </summary>
public sealed record SshItem
{
    /// <summary>The port ssh connects to when the item declares none.</summary>
    public const int DefaultPort = 22;

    /// <summary>The item's name, which is the name <c>hosts.ssh</c> and <c>--ssh</c> use.</summary>
    public required string Name { get; init; }

    /// <summary>The machine's address: a name the machine's network resolves, or a literal address.</summary>
    public required string Address { get; init; }

    /// <summary>The user ssh logs in as.</summary>
    public required string User { get; init; }

    /// <summary>The port ssh connects to.</summary>
    public required int Port { get; init; }

    /// <summary>The private key file, offered to the host and to no other.</summary>
    public required string KeyFile { get; init; }

    /// <summary>
    /// The file holding the host keys ssh may accept for this item. Given to ssh explicitly, so the
    /// user's own <c>~/.ssh/known_hosts</c> decides nothing here, and a host key that changed is
    /// refused by the file this repository set up rather than by whatever a machine happened to trust.
    /// </summary>
    public required string KnownHostsFile { get; init; }

    /// <summary>
    /// The superuser credential for installing tools there, when the item declares one. Absent for a
    /// host that needs none, and a privileged install without one is refused by name rather than
    /// attempted with nothing.
    /// </summary>
    public HostCredential? Superuser { get; init; }
}

/// <summary>
/// What one WSL distribution's directory under <c>.harness-config/wslDistros</c> declares. The
/// distribution's own name lives here rather than in <c>config.json</c> for the same reason an ssh
/// host's address does: it names a machine, and the tracked configuration names none.
/// </summary>
public sealed record WslItem
{
    /// <summary>The item's name, which is the name <c>hosts.wsl</c> and <c>--wsl</c> use.</summary>
    public required string Name { get; init; }

    /// <summary>The distribution, spelt as WSL lists it, which is what <c>wsl.exe --distribution</c> receives.</summary>
    public required string Distribution { get; init; }

    /// <summary>The superuser credential for installing tools in the distribution, when it declares one.</summary>
    public HostCredential? Superuser { get; init; }
}

/// <summary>What reading one item's directory found.</summary>
/// <typeparam name="TItem">What that directory declares.</typeparam>
/// <param name="Item">The item, or <see langword="null"/> when the directory could not be used.</param>
/// <param name="Problem">
/// Why it could not be used, naming the file and the key at fault; empty when it could. A refusal
/// carries the file and the key because "the host is unavailable" sends a reader to the network, and
/// the fault is a line in a file on this machine.
/// </param>
public sealed record SecretsRead<TItem>(TItem? Item, string Problem)
    where TItem : class;
