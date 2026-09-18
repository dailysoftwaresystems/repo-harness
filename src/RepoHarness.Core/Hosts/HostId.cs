using System.Diagnostics.CodeAnalysis;

namespace RepoHarness.Core.Hosts;

/// <summary>How a host is reached.</summary>
public enum HostKind
{
    /// <summary>The machine running the harness.</summary>
    Local,

    /// <summary>A WSL distribution on that machine, reached through wsl.exe.</summary>
    Wsl,

    /// <summary>A machine reached over ssh.</summary>
    Ssh,
}

/// <summary>
/// A host, named the way the command line selects it: this machine, <c>--wsl &lt;distro&gt;</c> or
/// <c>--ssh &lt;name&gt;</c>. Names compare ignoring case, as configuration keys do.
/// </summary>
public sealed class HostId : IEquatable<HostId>
{
    private HostId(HostKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>The machine running the harness.</summary>
    public static HostId Local { get; } = new(HostKind.Local, "local");

    /// <summary>How the host is reached.</summary>
    public HostKind Kind { get; }

    /// <summary>The distribution or ssh host name; <c>local</c> for this machine.</summary>
    public string Name { get; }

    /// <summary>A WSL distribution.</summary>
    public static HostId Wsl(string distribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distribution);
        return new HostId(HostKind.Wsl, distribution);
    }

    /// <summary>An ssh host.</summary>
    public static HostId Ssh(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new HostId(HostKind.Ssh, name);
    }

    public bool Equals(HostId? other)
        => other is not null
            && Kind == other.Kind
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as HostId);

    public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(Name));

    /// <summary>
    /// The physical machine this host runs on, which two hosts share when they contend for one
    /// machine's processors, memory and disk.
    /// </summary>
    /// <remarks>
    /// A WSL distribution runs on the machine running the harness, so it and <see cref="Local"/> are
    /// one machine however many distributions are declared; each ssh host is its own. This is what
    /// legs are chunked by, because two legs on one machine take that machine's cores from each
    /// other whatever their operating systems say, and a build timed while another build had the
    /// same processors measured something nobody asked about. Two ssh names that happen to reach one
    /// machine are counted as two, because nothing here can tell that they do.
    /// </remarks>
    public string MachineKey => Kind == HostKind.Ssh ? $"ssh:{Name.ToLowerInvariant()}" : "machine:local";

    /// <summary>The host as the command line names it: <c>local</c>, <c>wsl Ubuntu</c>, <c>ssh vps</c>.</summary>
    public override string ToString() => Kind switch
    {
        HostKind.Local => "local",
        HostKind.Wsl => $"wsl {Name}",
        _ => $"ssh {Name}",
    };

    /// <summary>
    /// Reads a host as <see cref="ToString"/> spells it, which is how a machine that dispatches a leg
    /// tells the host it sends it to which host that is.
    /// </summary>
    /// <param name="text">The host as it was spelled.</param>
    /// <param name="host">The host, when <paramref name="text"/> names one.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HostId? host)
    {
        host = (text?.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []) switch
        {
            ["local"] => Local,
            ["wsl", var distribution] => Wsl(distribution),
            ["ssh", var name] => Ssh(name),
            _ => null,
        };

        return host is not null;
    }
}
