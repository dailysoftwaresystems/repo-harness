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

    /// <summary>The host as the command line names it: <c>local</c>, <c>wsl Ubuntu</c>, <c>ssh vps</c>.</summary>
    public override string ToString() => Kind switch
    {
        HostKind.Local => "local",
        HostKind.Wsl => $"wsl {Name}",
        _ => $"ssh {Name}",
    };
}
