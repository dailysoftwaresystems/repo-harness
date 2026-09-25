using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
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
/// <param name="stopsWithin">How long an ended hold takes to stop; <see cref="Execution.HoldAwakeService.PollInterval"/> unless a test says.</param>
public sealed class HoldAwakeRegistry(IHostCommandRunner hostCommands, IHarnessOutput output, TimeSpan? stopsWithin = null)
{
    /// <summary>How long a host has to answer a hold: it starts one process and answers at once.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(1);

    /// <summary>How long a hold takes to stop once ended: it reads its state again this often.</summary>
    public TimeSpan StopsWithin { get; } = stopsWithin ?? Execution.HoldAwakeService.PollInterval;

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

    /// <summary>
    /// Ends the hold on the host <paramref name="connection"/> reaches, where one stands, through the DssHarness
    /// at <paramref name="toolPath"/> there; whether that DssHarness said it had. One too old to know holds has
    /// none to end, and says no.
    /// </summary>
    /// <param name="connection">The connection to the host.</param>
    /// <param name="toolPath">Where DssHarness is on the host.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    public async Task<bool> EndAsync(HostConnection connection, string toolPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPath);

        try
        {
            var (finished, _, _) = await AskAsync(
                    connection,
                    toolPath,
                    nonce => new HostAgentRequest { Kind = HostAgentRequestKind.Hold, HoldAwakeSeconds = 0, Nonce = nonce },
                    cancellationToken)
                .ConfigureAwait(false);

            return finished == HarnessExit.Success;
        }
        catch (HarnessException)
        {
            return false;
        }
    }

    private async Task HoldAsync(string commandName, HostReport host, CancellationToken cancellationToken)
    {
        var session = host.Session!;

        try
        {
            var (finished, said, result) = await AskAsync(
                    session.Connection,
                    session.ToolPath,
                    nonce => new HostAgentRequest
                    {
                        Kind = HostAgentRequestKind.Hold,
                        HoldAwakeSeconds = host.HoldAwakeSeconds,
                        KeepAwake = [.. host.KeepAwake],
                        KeepAwakeEnvironment = new(host.KeepAwakeEnvironment, StringComparer.Ordinal),
                        KeepAwakeDirectories = [.. host.ProgramDirectories],
                        Nonce = nonce,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (finished == HarnessExit.Success)
            {
                _output.Detail(
                    commandName,
                    string.Create(CultureInfo.InvariantCulture, $"{host.Host}: held awake for up to {host.HoldAwakeSeconds} seconds, until a command's own keepAwake takes over"));
                return;
            }

            _output.Warn(
                commandName,
                $"{host.Host}: could not be held awake until the next command: "
                + (said ?? (finished is { } code ? $"it exited {code}" : HostProbes.NeverFinished("the hold", result, session.Connection))));
        }
        catch (HarnessException ex)
        {
            _output.Warn(commandName, $"{host.Host}: could not be held awake until the next command: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>
    /// Sends the DssHarness at <paramref name="toolPath"/> the hold request <paramref name="requestFor"/> makes
    /// with a nonce of its own, and says how it finished, what it refused with, and what the transport did.
    /// </summary>
    private async Task<(int? Finished, string? Said, ProcessResult Result)> AskAsync(
        HostConnection connection,
        string toolPath,
        Func<string, HostAgentRequest> requestFor,
        CancellationToken cancellationToken)
    {
        var nonce = HostAgentProtocol.NewNonce();
        var lines = new HostAgentLines(nonce);
        string? said = null;

        var result = await _hostCommands.RunAsync(
                connection,
                new HostCommand
                {
                    Program = toolPath,
                    Arguments = [HostAgentProtocol.CommandName],
                    StandardInput = JsonSerializer.Serialize(requestFor(nonce), HostAgentProtocol.JsonOptions) + "\n",
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

        return (lines.Finished, said, result);
    }
}
