using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// What ssh would do to reach a host, as <c>ssh -G</c> prints it: every setting it would connect with once
/// its own configuration and the command line have been read, one to a line.
/// </summary>
/// <remarks>
/// Asked rather than assumed: ssh reads the user's and the system's own configuration for whatever the
/// command line leaves unsaid - a HostName mapping the name declared to another, a ProxyJump or a
/// ProxyCommand that reaches the host through something else, a Match block keyed by the name ssh looks up.
/// Measured with OpenSSH for Windows 9.5p2 and 10.0p2 and Git for Windows' 10.5p1: each prints the name it
/// would look up as <c>hostname</c>, with a mapping and its tokens applied, and prints <c>proxyjump</c>,
/// <c>proxycommand</c> and <c>hostkeyalias</c> only where they are set.
/// </remarks>
public sealed class SshSettings
{
    /// <summary>The settings that say only where a connection goes, which a pin changes and nothing else may.</summary>
    private static readonly string[] WhereItGoes = ["hostname", "hostkeyalias", "checkhostip"];

    private readonly IReadOnlyList<KeyValuePair<string, string>> _lines;

    private SshSettings(IReadOnlyList<KeyValuePair<string, string>> lines, string hostName)
    {
        _lines = lines;
        HostName = hostName;
    }

    /// <summary>The name, or the address, ssh would look up and connect to.</summary>
    public string HostName { get; }

    /// <summary>The name ssh looks the host's key up under in place of its own, where its configuration sets one.</summary>
    public string? HostKeyAlias => Value("hostkeyalias");

    /// <summary>
    /// Whether ssh reaches the host through something else - a jump host, or a command - which looks the name
    /// up on its own side, where an address this machine resolved it to means nothing.
    /// </summary>
    public bool Proxied => Value("proxyjump") is not null || Value("proxycommand") is not null;

    /// <summary>
    /// What <c>ssh -G</c> printed, read; <see langword="null"/> where it did not finish, failed, or named no
    /// hostname - where ssh could not say what it would do, and a connection is left to it unpinned.
    /// </summary>
    /// <param name="result">What running <c>ssh -G</c> produced.</param>
    public static SshSettings? Read(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        var lines = result.StandardOutput
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.Split(' ', 2) is [var key, var value]
                ? KeyValuePair.Create(key.ToLowerInvariant(), value.Trim())
                : KeyValuePair.Create(line.ToLowerInvariant(), string.Empty))
            .ToList();

        return lines.FirstOrDefault(line => line.Key == "hostname").Value is { Length: > 0 } hostName
            ? new SshSettings(lines, hostName)
            : null;
    }

    /// <summary>
    /// Whether <paramref name="other"/> is these settings in all but where the connection goes: the name or
    /// address, the name the host's key is looked up under, and whether the address is checked as well.
    /// </summary>
    /// <param name="other">The settings to compare, read the same way.</param>
    /// <remarks>
    /// Measured: a Match block keyed by the host changes what ssh does once it is given an address in place
    /// of the name - one matching <c>192.0.2.*</c> turned agent forwarding on for a pinned connection that
    /// had it off. Compared whole, every such difference shows, whichever setting it touches.
    /// </remarks>
    public bool DifferOnlyInWhereTheyGo(SshSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Beyond(_lines).SequenceEqual(Beyond(other._lines));
    }

    private static IEnumerable<KeyValuePair<string, string>> Beyond(IEnumerable<KeyValuePair<string, string>> lines)
        => lines.Where(line => !WhereItGoes.Contains(line.Key, StringComparer.Ordinal));

    /// <summary>A setting's value, or <see langword="null"/> where it is absent, or ssh's word for unset.</summary>
    private string? Value(string key)
        => _lines.FirstOrDefault(line => line.Key == key).Value is { Length: > 0 } value
            && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
                ? value
                : null;
}

/// <summary>
/// An address ssh is given to dial in place of looking the host's name up, with the name it looks the host's
/// key up under in place of that address: what this machine resolved the name to, for every call a
/// connection makes.
/// </summary>
/// <remarks>
/// <para>
/// A name looked up by every ssh call is a name each call can fail to find: measured, a Mac in a dark wake
/// answered this machine's lookup and a shell probe over ssh, twice, and then failed the next call's own
/// lookup, and Git for Windows' own ssh resolves no mDNS <c>.local</c> name at all. Given the address as its
/// HostName, ssh resolves no name while the pin holds.
/// </para>
/// <para>
/// Shared by every copy of the connection it was made for, so that a pin that failed is dropped once, for all
/// of them, and each call after it lets ssh look the name up as it would have: see <see cref="Drop"/>.
/// </para>
/// </remarks>
/// <param name="address">The address ssh dials.</param>
/// <param name="keyAlias">The name ssh looks the host's key up under.</param>
public sealed class SshPin(string address, string keyAlias)
{
    private int _dropped;

    /// <summary>The address ssh dials.</summary>
    public string Address { get; } = address;

    /// <summary>
    /// The name ssh looks the host's key up under: the one it looks up unpinned - its configuration's own
    /// alias, or the name it dials, spelt <c>[name]:port</c> off port 22 as known_hosts spells such a host.
    /// </summary>
    public string KeyAlias { get; } = keyAlias;

    /// <summary>Whether ssh is still given the address: until a call over it fails before any session begins.</summary>
    public bool Holds => Volatile.Read(ref _dropped) == 0;

    /// <summary>
    /// Stops giving ssh the address, for every later call the connection makes. Dropped when a call over it
    /// failed before any session began - the address did not take the connection, or showed a key the name
    /// is not known by: a machine that moved, one that answers at another of its addresses, or a key ssh
    /// would have checked another way.
    /// </summary>
    public void Drop() => Interlocked.Exchange(ref _dropped, 1);

    /// <summary>
    /// The pin for a connection to <paramref name="settings"/>'s host on <paramref name="port"/>, to
    /// <paramref name="address"/>; <see langword="null"/> where that is the address ssh would dial anyway.
    /// </summary>
    /// <param name="settings">What ssh would do unpinned.</param>
    /// <param name="address">What this machine resolved <see cref="SshSettings.HostName"/> to.</param>
    /// <param name="port">The port the connection is made on.</param>
    public static SshPin? For(SshSettings settings, string address, int port)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (string.Equals(address, settings.HostName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SshPin(
            address,
            settings.HostKeyAlias ?? (port == Secrets.SshItem.DefaultPort ? settings.HostName : $"[{settings.HostName}]:{port}"));
    }

    /// <summary>
    /// The options that pin a connection: the address as its HostName, the key looked up under the alias, and
    /// the address neither looked up in known_hosts nor written into it.
    /// </summary>
    /// <remarks>
    /// ssh expands tokens in HostName, so the <c>%</c> of an IPv6 address's scope is doubled: measured, given
    /// <c>fe80::1%12</c> ssh refused it as an unknown token, and given <c>fe80::1%%12</c> it took the address.
    /// The destination stays the name declared, so every Host block written for it still applies.
    /// </remarks>
    internal string[] Options()
        =>
        [
            "-o", $"HostName={Address.Replace("%", "%%", StringComparison.Ordinal)}",
            "-o", $"HostKeyAlias={KeyAlias}",
            "-o", "CheckHostIP=no",
        ];
}
