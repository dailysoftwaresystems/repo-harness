using RepoHarness.Core.Hosts;

namespace RepoHarness.Core.Tools;

/// <summary>The exit code <c>install-missing-tools</c> uses for an answer of "no".</summary>
/// <remarks>
/// From the range reserved for a command's own contract: a tool that is missing and cannot be
/// installed is what the command was asked to find out, not a failure of the command. A host that
/// could not be reached keeps the shared <c>HostUnavailable</c> code, because nothing ran there.
/// </remarks>
public static class ToolsExit
{
    /// <summary>A declared tool is missing, out of date, or could not be installed on some leg's host.</summary>
    public const int NotProvisioned = 1;
}

/// <summary>What provisioning established about one tool on one host.</summary>
public enum ToolState
{
    /// <summary>The host could not be asked, so nothing is known about it there.</summary>
    Unknown,

    /// <summary>It was already installed, and at or above the version the configuration requires.</summary>
    AlreadyCurrent,

    /// <summary>It was not there, and this run installed it.</summary>
    Installed,

    /// <summary>It was below the version required, and this run brought it up.</summary>
    Updated,

    /// <summary>It is not there, and nothing declares how to install it, so it was only reported.</summary>
    Missing,

    /// <summary>It is below the version required, and nothing declares how to install it.</summary>
    Outdated,

    /// <summary>Installing or updating it was attempted and did not work.</summary>
    Failed,
}

/// <summary>What provisioning did about one tool on one host.</summary>
/// <param name="Tool">The tool, as <c>tools</c> names it.</param>
/// <param name="State">What was established or done.</param>
/// <param name="Version">The version found there, when one was read.</param>
/// <param name="Detail">
/// What to do about it, or what went wrong, already phrased for a reader. Never carries what a
/// privileged command was given: a credential that reached a report would be in every log that kept it.
/// </param>
public sealed record ToolOutcome(string Tool, ToolState State, string? Version = null, string? Detail = null)
{
    /// <summary>Whether the tool is now installed and current on that host.</summary>
    public bool Ready => State is ToolState.AlreadyCurrent or ToolState.Installed or ToolState.Updated;

    /// <summary>What the state is called in a report, which is also what a second run is expected to say.</summary>
    public string StateName => State switch
    {
        ToolState.AlreadyCurrent => "already current",
        ToolState.Installed => "installed",
        ToolState.Updated => "updated",
        ToolState.Missing => "missing",
        ToolState.Outdated => "outdated",
        ToolState.Failed => "failed",
        _ => "unknown",
    };
}

/// <summary>What provisioning found for one leg, on the host that leg would run on.</summary>
public sealed record LegProvision
{
    /// <summary>The leg, as the configuration names it.</summary>
    public required string Leg { get; init; }

    /// <summary>
    /// The host its tools were provisioned on: the one the leg names with <c>wsl</c> or <c>ssh</c>, and
    /// this machine otherwise. A leg that names no host is provisioned here because nothing has been
    /// measured yet, and this machine is the host every such leg prefers.
    /// </summary>
    public required HostId Host { get; init; }

    /// <summary>Why nothing could be done there, or <see langword="null"/> when the host answered.</summary>
    public string? Unreachable { get; init; }

    /// <summary>What was done about each tool there, in the order the configuration declares them.</summary>
    public IReadOnlyList<ToolOutcome> Tools { get; init; } = [];

    /// <summary>Whether every tool this leg needs is installed and current on its host.</summary>
    public bool Provisioned => Unreachable is null && Tools.All(tool => tool.Ready);
}

/// <summary>What provisioning every selected leg found.</summary>
/// <param name="Legs">One entry per selected leg, in selection order.</param>
public sealed record ToolProvisionReport(IReadOnlyList<LegProvision> Legs)
{
    /// <summary>Whether every selected leg has every tool it needs.</summary>
    public bool Passed => Legs.All(leg => leg.Provisioned);

    /// <summary>Whether a host could not be reached, which is a different answer from a tool that is missing.</summary>
    public bool AnyUnreachable => Legs.Any(leg => leg.Unreachable is not null);
}
