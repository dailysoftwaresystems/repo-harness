using RepoHarness.Core.Output;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Whether a command typed in a copy the harness synced to a host was refused a host, and the one notice that
/// then says where the command belongs.
/// </summary>
/// <remarks>
/// Inside a copy every host is refused for the same underlying reason, so the notice is said once, as the
/// command ends, after each host's own refusal - never after each of them. A consumer's command whose legs
/// named several hosts said the same paragraph after every refusal, and again in its conclusion.
/// </remarks>
/// <param name="output">Says the notice.</param>
public sealed class SyncedCopyRefusals(IHarnessOutput output)
{
    private readonly IHarnessOutput _output = output;
    private int _refused;

    /// <summary>Records that a host could not be reached from a synced copy.</summary>
    public void Refused() => Interlocked.Exchange(ref _refused, 1);

    /// <summary>Says <see cref="HostConnector.SyncedCopyNotice"/> once, where a host was refused since it was last said.</summary>
    /// <param name="commandName">The command ending, which prefixes the notice.</param>
    public void SayOnce(string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        if (Interlocked.Exchange(ref _refused, 0) == 1)
        {
            _output.Warn(commandName, HostConnector.SyncedCopyNotice + ".");
        }
    }
}
