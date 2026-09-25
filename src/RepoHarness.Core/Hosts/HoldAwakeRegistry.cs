using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// The ssh hosts a command reached that are to be held awake between commands, and the hold it leaves each of
/// them with as it ends.
/// </summary>
/// <remarks>
/// Each command is a process of its own, and every keepAwake it starts on a host ends with the connection that
/// started it: a personal Mac falls back asleep in the seconds before the next command, which then cannot find
/// it. A host that declares <c>holdAwakeSeconds</c> is therefore asked, as a command finishes with it, to hold
/// itself awake that long - until a command's own keepAwake takes over there, which ends the hold. Recorded as
/// each host is reached, so a host is held however the command then ended, and one that was never reached is
/// asked nothing. A hold that cannot be left is said, and fails nothing: the command's own work is done.
/// </remarks>
/// <param name="hostCommands">Asks the DssHarness on a host.</param>
/// <param name="output">Says what was left, and what could not be.</param>
public sealed class HoldAwakeRegistry(IHostCommandRunner hostCommands, IHarnessOutput output)
{
    /// <summary>How long a host has to answer a hold: it starts one process and answers at once.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<HostId, HostReport> _reached = new();
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;

    /// <summary>Records <paramref name="host"/> as reached, to be held when the command ends, where it asks for a hold.</summary>
    /// <param name="host">What measuring a host found.</param>
    public void Reached(HostReport host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (host is { Host.Kind: HostKind.Ssh, HoldAwakeSeconds: > 0, KeepAwake.Count: > 0, Session: not null })
        {
            _reached[host.Host] = host;
        }
    }

    /// <summary>Asks each host this command reached that asks for a hold to hold itself awake, all at once.</summary>
    /// <param name="commandName">The command ending, which prefixes what is said.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    public Task LeaveHoldsAsync(string commandName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        return Task.WhenAll(_reached.Values.Select(host => HoldAsync(commandName, host, cancellationToken)));
    }

    private async Task HoldAsync(string commandName, HostReport host, CancellationToken cancellationToken)
    {
        var session = host.Session!;
        var nonce = HostAgentProtocol.NewNonce();
        var lines = new HostAgentLines(nonce);
        string? said = null;

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Hold,
                HoldAwakeSeconds = host.HoldAwakeSeconds,
                KeepAwake = [.. host.KeepAwake],
                KeepAwakeEnvironment = new(host.KeepAwakeEnvironment, StringComparer.Ordinal),
                KeepAwakeDirectories = [.. host.ProgramDirectories],
                Nonce = nonce,
            },
            HostAgentProtocol.JsonOptions);

        try
        {
            var result = await _hostCommands.RunAsync(
                    session.Connection,
                    new HostCommand
                    {
                        Program = session.ToolPath,
                        Arguments = [HostAgentProtocol.CommandName],
                        StandardInput = request + "\n",
                        Timeout = Budget,
                        OnErrorLine = line =>
                        {
                            if (lines.Error(line) && FailureLine.TryRead(line, HostAgentProtocol.CommandName, out var refusal))
                            {
                                said = refusal;
                            }
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (lines.Finished == HarnessExit.Success)
            {
                _output.Detail(
                    commandName,
                    string.Create(CultureInfo.InvariantCulture, $"{host.Host}: held awake for up to {host.HoldAwakeSeconds} seconds, until a command's own keepAwake takes over"));
                return;
            }

            _output.Warn(
                commandName,
                $"{host.Host}: could not be held awake until the next command: "
                + (said ?? (lines.Finished is { } code ? $"it exited {code}" : HostProbes.NeverFinished("the hold", result, session.Connection))));
        }
        catch (HarnessException ex)
        {
            _output.Warn(commandName, $"{host.Host}: could not be held awake until the next command: {ex.Message.TrimEnd('.')}");
        }
    }
}
