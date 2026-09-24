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
public sealed class KeepAwake
{
    /// <summary>The one name a <c>keepAwake</c> command is filled in with: the process running the leg.</summary>
    public const string ProcessName = "pid";

    /// <summary>
    /// How long a stopped command is waited for. Bounded: a descendant the stop cannot reach - one
    /// running as root under sudo - keeps its output open, and a leg whose work is done would
    /// otherwise wait on it forever.
    /// </summary>
    public static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _processRunner;
    private readonly IHarnessOutput _output;
    private readonly TimeSpan _stopBudget;
    private readonly HoldAwakeStore? _holds;

    /// <summary>Holds machines awake with <paramref name="processRunner"/>, saying what goes wrong through <paramref name="output"/>.</summary>
    public KeepAwake(IProcessRunner processRunner, IHarnessOutput output)
        : this(processRunner, output, StopBudget)
    {
    }

    /// <summary>The same, ending the hold <paramref name="holds"/> keeps when a command of its own starts.</summary>
    /// <param name="processRunner">Starts the command.</param>
    /// <param name="output">Says what goes wrong.</param>
    /// <param name="holds">Where this machine's hold between commands is kept.</param>
    public KeepAwake(IProcessRunner processRunner, IHarnessOutput output, HoldAwakeStore holds)
        : this(processRunner, output, StopBudget)
    {
        _holds = holds;
    }

    /// <summary>The same, waiting <paramref name="stopBudget"/> for a stopped command.</summary>
    internal KeepAwake(IProcessRunner processRunner, IHarnessOutput output, TimeSpan stopBudget)
    {
        _processRunner = processRunner;
        _output = output;
        _stopBudget = stopBudget;
    }

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
    /// <param name="endsHold">
    /// Whether starting the command ends the hold that keeps this machine awake between commands: always, but
    /// for that hold's own command.
    /// </param>
    /// <remarks>
    /// A hold exists between commands, and never during one: the command a leg or a request starts here holds
    /// the machine itself, so the hold ends the moment it starts, and a command that holds nothing - one that
    /// declares no keepAwake - leaves it standing.
    /// </remarks>
    public IAsyncDisposable Hold(
        string commandName,
        string leg,
        HostSettings host,
        IReadOnlyList<string> programDirectories,
        CancellationToken cancellationToken,
        bool endsHold = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(programDirectories);

        if (host.KeepAwake is not [var program, .. var arguments])
        {
            return Released.Instance;
        }

        if (endsHold)
        {
            _holds?.End();
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

        return new Holding(this, commandName, leg, Watch(commandName, leg, running, stop.Token), stop);
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
    private sealed class Holding(KeepAwake owner, string commandName, string leg, Task watching, CancellationTokenSource stop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync().ConfigureAwait(false);

            try
            {
                await watching.WaitAsync(owner._stopBudget).ConfigureAwait(false);
                stop.Dispose();
            }
            catch (TimeoutException)
            {
                // Said, and left behind: the leg's work is done, and its verdict does not wait on a
                // command the stop could not reach.
                owner._output.Warn(commandName, $"{leg}: keepAwake did not stop within {owner._stopBudget.TotalSeconds:0} seconds, and may still be holding this machine awake.");
            }
        }
    }

    /// <summary>What a machine that declares no command is held with: nothing.</summary>
    private sealed class Released : IAsyncDisposable
    {
        public static Released Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
