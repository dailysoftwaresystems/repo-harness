using System.Globalization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Execution;

/// <summary>
/// Holds the machine a leg runs on awake while the leg's own work runs there, with the command that
/// machine declares under <c>keepAwake</c>.
/// </summary>
/// <remarks>
/// A host that sleeps mid-leg charges the sleep to whatever was running, which once reported a 4 ms
/// test at 729 s. Held on the machine the work runs on - a leg dispatched to a host is held awake by
/// the DssHarness there, under that host's own section - and released when the work ends, however it
/// ended. A command that cannot hold the machine awake is said, and fails nothing: a sleep it did not
/// prevent is still seen, as wall time outrunning the monotonic clock, and marks the phase it
/// interrupted suspect, as it does on a machine that declares no command at all.
/// </remarks>
public sealed class KeepAwake(IProcessRunner processRunner, IHarnessOutput output)
{
    /// <summary>The one name a <c>keepAwake</c> command is filled in with: the process running the leg.</summary>
    public const string ProcessName = "pid";

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IHarnessOutput _output = output;

    /// <summary>The names a <c>keepAwake</c> command is filled in with, for the process <paramref name="processId"/>.</summary>
    /// <param name="processId">The DssHarness process running the leg.</param>
    public static IReadOnlyDictionary<string, string> Names(int processId)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProcessName] = processId.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Starts <paramref name="host"/>'s <c>keepAwake</c>, when it declares one, and returns what
    /// stops it.
    /// </summary>
    /// <param name="commandName">The command running the leg, which prefixes what is said.</param>
    /// <param name="leg">The leg, as what is said names it.</param>
    /// <param name="host">What the machine the leg runs on declares for itself.</param>
    /// <param name="programDirectories">The directories the survey found this machine's programs in.</param>
    /// <param name="cancellationToken">Stops the command with the run.</param>
    public IAsyncDisposable Hold(
        string commandName,
        string leg,
        HostSettings host,
        IReadOnlyList<string> programDirectories,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(programDirectories);

        if (host.KeepAwake is not [var program, .. var arguments])
        {
            return Released.Instance;
        }

        // The process running the leg, which a command such as caffeinate -w waits on: should this
        // process end without stopping it, it stops by itself.
        var names = Names(Environment.ProcessId);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var running = _processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = LegPathNames.Fill(program, names, "keepAwake"),
                Arguments = [.. arguments.Select(argument => LegPathNames.Fill(argument, names, "keepAwake"))],

                // A process the leg starts on this machine, so it starts as the leg's own programs
                // do: under the host's environment, and finding what the survey found.
                Environment = PhaseEnvironment.Layered(host.Env),
                AppendToPath = programDirectories,
            },
            stop.Token);

        return new Holding(Watch(commandName, leg, running, stop.Token), stop);
    }

    /// <summary>Says so when the command stops holding the machine awake before the leg's work is done.</summary>
    private async Task Watch(string commandName, string leg, Task<ProcessResult> running, CancellationToken released)
    {
        try
        {
            var result = await running.ConfigureAwait(false);

            if (!released.IsCancellationRequested)
            {
                var said = result.StandardError.Trim() is { Length: > 0 } error ? $": {error}" : string.Empty;

                _output.Warn(commandName, $"{leg}: keepAwake ended before the leg did (exit {result.ExitCode}){said}, so nothing holds this machine awake for the rest of it.");
            }
        }
        catch (OperationCanceledException) when (released.IsCancellationRequested)
        {
            // Released because the leg's work ended, or the run was interrupted: what it is for.
        }
        catch (ProgramStartException ex)
        {
            _output.Warn(commandName, $"{leg}: keepAwake could not start, so nothing holds this machine awake: {ex.Message}");
        }
    }

    /// <summary>A command holding the machine awake, stopped and waited for when the leg's work ends.</summary>
    private sealed class Holding(Task watching, CancellationTokenSource stop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await watching.ConfigureAwait(false);
            stop.Dispose();
        }
    }

    /// <summary>What a machine that declares no command is held with: nothing.</summary>
    private sealed class Released : IAsyncDisposable
    {
        public static Released Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
