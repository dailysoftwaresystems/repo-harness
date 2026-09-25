using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// Answers every command a host would receive from a script, and records what was asked. A real
/// WSL distribution or ssh host would make each rule depend on what one machine has installed.
/// </summary>
internal sealed class ScriptedHostCommands(Func<HostConnection, HostCommand, ProcessResult> respond) : IHostCommandRunner
{
    private readonly List<(HostConnection Connection, HostCommand Command)> _calls = [];
    private readonly List<HostConnection> _shellProbes = [];
    private readonly List<HostConnection> _settingsReads = [];

    /// <summary>
    /// What <c>ssh -G</c> prints for a connection; by default what ssh with no configuration of the user's
    /// prints, as <see cref="PrintedSettings"/> writes it.
    /// </summary>
    public Func<HostConnection, ProcessResult> SettingsPrinted { get; set; } = connection => HostResults.Ok(PrintedSettings(connection));

    /// <summary>
    /// What <c>ssh -G</c> prints for <paramref name="connection"/>, as the clients measured print it: the
    /// address it is given as its hostname - or a holding pin's address, with the pin's alias - on the
    /// connection's port, with <paramref name="configured"/>, what the user's own configuration adds, after.
    /// </summary>
    public static string PrintedSettings(HostConnection connection, string configured = "")
    {
        var pin = connection.Pin is { Holds: true } holding ? holding : null;

        return $"user {connection.User}\nhostname {pin?.Address ?? connection.Address}\nport {connection.Port}\n"
            + (pin is null ? string.Empty : $"hostkeyalias {pin.KeyAlias}\n")
            + "checkhostip no\nforwardagent no\n"
            + configured;
    }

    /// <summary>Every connection ssh was asked what it would do over, in order.</summary>
    public IReadOnlyList<HostConnection> SettingsReads
    {
        get
        {
            lock (_calls)
            {
                return [.. _settingsReads];
            }
        }
    }

    /// <summary>
    /// What the host's login shell prints on both streams before its agent runs, or <see langword="null"/>
    /// for a host whose profile is quiet.
    /// </summary>
    /// <remarks>
    /// A shell startup writes to the same streams the command does, and does it first: one consumer's Mac
    /// sources emsdk's environment script on every session, which prints the account's home layout.
    /// </remarks>
    public Func<HostCommand, string>? LoginShellPrints { get; set; }

    /// <summary>
    /// Whether the agent marks where its own output begins. A host that refuses a request before it can read
    /// one - an empty request, a protocol it does not speak - writes its refusal and no marker at all, so the
    /// machine that asked must still hear it.
    /// </summary>
    public bool Marks { get; set; } = true;

    /// <summary>
    /// Whether the host's login shell ends its last write without a newline, so that what it printed is glued
    /// onto the first line the agent writes - the marker.
    /// </summary>
    public bool LoginShellLeavesALineOpen { get; set; }

    /// <summary>What the ssh shell probe answers; by default a shell that is not cmd.</summary>
    public ProcessResult ShellProbe { get; set; } = HostResults.Ok("%COMSPEC%\n");

    /// <summary>What the shell probe answers first, one per probe, before <see cref="ShellProbe"/>: a host that takes a while to wake.</summary>
    public Queue<ProcessResult> ShellProbesFirst { get; } = new();

    /// <summary>
    /// Writes <paramref name="output"/> where a host writes what a command it ran said: standard
    /// output. Standard error carries the protocol's completion line, so an answer written there would
    /// be read as a transport message.
    /// </summary>
    /// <param name="command">The command being answered.</param>
    /// <param name="output">What it says, line by line.</param>
    public static void Answer(HostCommand command, string output)
    {
        foreach (var line in output.Split('\n'))
        {
            command.OnOutputLine?.Invoke(line.TrimEnd('\r'));
        }
    }

    /// <summary>What the ssh shell probe raises instead of answering, when set: ssh that would not start.</summary>
    public Exception? ShellProbeRaises { get; set; }

    /// <summary>What asking WSL for its default distribution answers; by default, WSL is not expected to be asked.</summary>
    public Func<ProcessResult> DefaultWslDistribution { get; set; }
        = () => throw new InvalidOperationException("WSL was not expected to be asked for its default distribution.");

    /// <summary>Every command run, in order.</summary>
    public IReadOnlyList<(HostConnection Connection, HostCommand Command)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Every connection the ssh shell probe ran over: the first thing that reaches an ssh host.</summary>
    public IReadOnlyList<HostConnection> ShellProbes
    {
        get
        {
            lock (_calls)
            {
                return [.. _shellProbes];
            }
        }
    }

    /// <summary>The one command whose arguments start with <paramref name="leadingArguments"/>.</summary>
    public HostCommand Single(params string[] leadingArguments)
        => Assert.Single(Calls, call => call.Command.Arguments.Take(leadingArguments.Length).SequenceEqual(leadingArguments)).Command;

    public Task<ProcessResult> RunAsync(HostConnection connection, HostCommand command, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _calls.Add((connection, command));
        }

        // The host's own shell first, as it comes: before the agent has run, and so before its marker. A
        // profile whose last write has no newline glues those bytes onto the marker, which is the next thing
        // written, exactly as a reader splitting on newlines would deliver it.
        var printed = LoginShellPrints?.Invoke(command).Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var glued = string.Empty;

        for (var index = 0; index < printed.Length; index++)
        {
            if (LoginShellLeavesALineOpen && index == printed.Length - 1)
            {
                glued = printed[index];
                break;
            }

            command.OnOutputLine?.Invoke(printed[index]);
            command.OnErrorLine?.Invoke(printed[index]);
        }

        // Then the agent marks its own output, as it does before serving: the machine that asked relays
        // nothing until it has seen this, so what a profile said is not taken for the run's output. A host
        // that refused before it could read the request writes no marker at all.
        if (Marks)
        {
            Mark(command, glued);
        }

        return Task.FromResult(respond(connection, command));
    }

    /// <summary>Writes the agent's start marker on both streams, where the command carries a request with a nonce.</summary>
    private static void Mark(HostCommand command, string glued = "")
    {
        if (string.IsNullOrEmpty(command.StandardInput))
        {
            return;
        }

        HostAgentRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (request?.Nonce is not { Length: > 0 } nonce)
        {
            return;
        }

        var started = glued + HostAgentProtocol.StartedLine(nonce);
        command.OnOutputLine?.Invoke(started);
        command.OnErrorLine?.Invoke(started);
    }

    public Task<ProcessResult> ProbeShellAsync(HostConnection connection, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _shellProbes.Add(connection);
        }

        return ShellProbeRaises is { } raised
            ? Task.FromException<ProcessResult>(raised)
            : Task.FromResult(ShellProbesFirst.TryDequeue(out var first) ? first : ShellProbe);
    }

    public Task<ProcessResult> ProbeDefaultWslDistributionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultWslDistribution());

    public Task<ProcessResult> ReadSshSettingsAsync(HostConnection connection, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _settingsReads.Add(connection);
        }

        return Task.FromResult(SettingsPrinted(connection));
    }
}

/// <summary>Reports a fixed measurement for each host, and records which hosts were measured, and for which emulators.</summary>
internal sealed class RecordingInspector(Func<HostId, HostReport> report) : IHostInspector
{
    private readonly List<(HostId Host, IReadOnlyDictionary<string, EmulatorConfig> Emulators, IReadOnlyDictionary<string, DeveloperEnvironmentConfig> Environments, IReadOnlyList<string> Programs, RoomQuestions Room)> _inspected = [];

    /// <summary>What each measurement was asked about the room on the host, in the order the hosts were measured.</summary>
    public IReadOnlyList<(HostId Host, RoomQuestions Room)> RoomAsked
    {
        get
        {
            lock (_inspected)
            {
                return [.. _inspected.Select(entry => (entry.Host, entry.Room))];
            }
        }
    }

    /// <summary>
    /// What a host answers about each build directory it is asked about, where a test scripts it; unscripted,
    /// a host answers for none.
    /// </summary>
    public Func<HostId, string, BuildDirectoryRoom>? BuildRooms { get; init; }

    /// <summary>Every host measured, in order.</summary>
    public IReadOnlyList<HostId> Inspected
    {
        get
        {
            lock (_inspected)
            {
                return [.. _inspected.Select(entry => entry.Host)];
            }
        }
    }

    /// <summary>The emulators each measurement was asked to check, in the order the hosts were measured.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, EmulatorConfig>> EmulatorsAsked
    {
        get
        {
            lock (_inspected)
            {
                return [.. _inspected.Select(entry => entry.Emulators)];
            }
        }
    }

    /// <summary>The developer environments each measurement was asked to look for, in the order the hosts were measured.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, DeveloperEnvironmentConfig>> DeveloperEnvironmentsAsked
    {
        get
        {
            lock (_inspected)
            {
                return [.. _inspected.Select(entry => entry.Environments)];
            }
        }
    }

    /// <summary>The programs each measurement was asked to find, in the order the hosts were measured.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ProgramsAsked
    {
        get
        {
            lock (_inspected)
            {
                return [.. _inspected.Select(entry => entry.Programs)];
            }
        }
    }

    public Task<HostReport> InspectAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        IReadOnlyDictionary<string, DeveloperEnvironmentConfig> developerEnvironments,
        IReadOnlyList<string> programs,
        RoomQuestions? room = null,
        CancellationToken cancellationToken = default)
    {
        lock (_inspected)
        {
            _inspected.Add((host, emulators, developerEnvironments, programs, room ?? RoomQuestions.None));
        }

        var answer = report(host);

        if (BuildRooms is { } rooms && answer.Available)
        {
            answer = answer with { Builds = [.. (room ?? RoomQuestions.None).Builds.Select(path => rooms(host, path))] };
        }

        // Likewise a host that says nothing about developer environments can set up every one it was
        // asked about; a test about one missing scripts it.
        if (answer.Available && answer.DeveloperEnvironments.Count == 0 && developerEnvironments.Count > 0)
        {
            answer = answer with
            {
                DeveloperEnvironments = developerEnvironments.Keys.ToDictionary(
                    name => name,
                    _ => DeveloperEnvironmentCheck.Installed(@"C:\Program Files\Microsoft Visual Studio\18\Enterprise", "18.10.12210.168"),
                    StringComparer.OrdinalIgnoreCase),
            };
        }

        // A scripted host that says nothing about programs has every one it was asked about, as a
        // real one answering the same question would say of a machine with everything installed. A
        // test about a missing program scripts the programs itself, and is answered as it wrote.
        return Task.FromResult(!answer.Available || answer.Programs.Count > 0
            ? answer
            : answer with
            {
                Programs = programs.ToDictionary(
                    program => program,
                    program => new ProgramLocation(program, ProgramFound.OnPath, "/usr/bin/" + program),
                    StringComparer.Ordinal),
            });
    }
}

/// <summary>Removes no copy from any host, and records none: a deletion that reaches no host.</summary>
internal sealed class NoHostCopies : IHostCopyRemover
{
    public Task<HostCopyRemoval> RemoveAsync(HarnessContext context, string worktree, string tree, HarnessConfig? treeConfig, CancellationToken cancellationToken = default)
        => Task.FromResult(HostCopyRemoval.None);
}

/// <summary>
/// Input that yields one line and then stays open until <see cref="End"/> is called, as a host's input does
/// while the machine that asked is still there.
/// </summary>
internal sealed class HeldOpenReader(string line) : TextReader
{
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _lineRead;

    /// <summary>Ends the input, as the machine that asked going away would.</summary>
    public void End() => _ended.TrySetResult();

    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_lineRead)
        {
            return new ValueTask<string?>(AtEndAsync<string?>(null, cancellationToken));
        }

        _lineRead = true;
        return ValueTask.FromResult<string?>(line);
    }

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        => new(AtEndAsync(0, cancellationToken));

    private async Task<T> AtEndAsync<T>(T atEnd, CancellationToken cancellationToken)
    {
        await _ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return atEnd;
    }
}

/// <summary>Process results for scripted hosts.</summary>
internal static class HostResults
{
    public static ProcessResult Ok(string output) => new(0, output, string.Empty, TimeSpan.Zero, TimedOut: false);

    public static ProcessResult Failed(int exitCode, string error) => new(exitCode, string.Empty, error, TimeSpan.Zero, TimedOut: false);

    public static InvalidOperationException Unexpected(HostCommand command)
        => new($"The host was not expected to run '{command.Program} {string.Join(' ', command.Arguments)}'.");

    /// <summary>What the runner that starts a host's transport raises when wsl.exe or ssh would not start.</summary>
    public static Core.Results.HarnessException TransportWouldNotStart(HostId host)
    {
        var start = TransportStart(host);

        return new(Core.Results.HarnessExit.HostUnavailable, $"{host} could not be reached: {start.Message}", start);
    }

    /// <summary>Why the transport that reaches <paramref name="host"/> would not start, in the words the system gives.</summary>
    public static ProgramStartException TransportStart(HostId host)
    {
        var transport = host.Kind == HostKind.Wsl ? "wsl" : "ssh";

        return new(transport, $"'{transport}' could not be started: The file cannot be accessed by the system.");
    }

    /// <summary>
    /// What the DssHarness on a host does with a run request: marks where its own output begins, passes on
    /// what the command wrote to standard error, says it finished with <paramref name="exitCode"/>, and
    /// exits with that code.
    /// </summary>
    /// <remarks>
    /// The marker goes on both streams, as the agent writes it, because the machine that asked relays
    /// nothing until it has seen one: a host's login shell writes to the same streams first, and what a
    /// profile says is not the run's output.
    /// </remarks>
    public static ProcessResult Finished(HostCommand command, int exitCode, string error = "")
    {
        var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions)
            ?? throw new InvalidOperationException("The host was sent no request.");

        var nonce = request.Nonce ?? throw new InvalidOperationException("The request carries no nonce.");
        var started = HostAgentProtocol.StartedLine(nonce);
        var completion = HostAgentProtocol.CompletionLine(nonce, exitCode);

        foreach (var line in error.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            command.OnErrorLine?.Invoke(line);
        }

        command.OnErrorLine?.Invoke(completion);

        return new ProcessResult(exitCode, string.Empty, started + "\n" + error + completion + "\n", TimeSpan.Zero, TimedOut: false);
    }
}

/// <summary>Stand-ins for what host and leg code reads about this machine, the repository, and its legs.</summary>
internal static class HostDoubles
{
    /// <summary>A machine that runs <paramref name="current"/> on <paramref name="processor"/>.</summary>
    public static IHostPlatform Platform(PlatformId current = PlatformId.Linux, string processor = "x86_64", string? home = null)
    {
        var platform = Substitute.For<IHostPlatform>();
        platform.Current.Returns(current);
        platform.PlatformKey.Returns(current switch
        {
            PlatformId.Windows => PlatformNames.Windows,
            PlatformId.Linux => PlatformNames.Linux,
            _ => throw new ArgumentOutOfRangeException(nameof(current), current, "Only Windows and Linux stand-ins are needed."),
        });
        platform.Processor.Returns(processor);
        platform.PathComparison.Returns(current == PlatformId.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        platform.HomeDirectory.Returns(home ?? TestHost.TemporaryRoot);
        return platform;
    }

    /// <summary>
    /// A loader that hands every command <paramref name="config"/>, for a tree at <paramref name="root"/>:
    /// a worktree of the main checkout at <paramref name="mainCheckoutRoot"/> when one is given.
    /// </summary>
    /// <param name="config">The configuration every load answers with.</param>
    /// <param name="root">The tree the context is for.</param>
    /// <param name="mainCheckoutRoot">The main checkout, where it is not <paramref name="root"/>.</param>
    /// <param name="syncedCopy">Whether the tree is a copy the harness synced to a host.</param>
    public static IHarnessContextLoader Loader(HarnessConfig config, string root, string? mainCheckoutRoot = null, bool syncedCopy = false)
    {
        var loader = Substitute.For<IHarnessContextLoader>();
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HarnessContext(new HarnessLayout(root, mainCheckoutRoot ?? root), config) { IsSyncedCopy = syncedCopy }));
        return loader;
    }

    /// <summary>A leg that needs <paramref name="os"/> on <paramref name="processor"/>, built in the "debug" configuration.</summary>
    public static LegConfig Leg(string os, string processor) => new() { Os = os, Processor = processor, Config = "debug" };
}

/// <summary>Records what this tool was asked to start detached, and starts nothing.</summary>
internal sealed class RecordingLauncher : IDetachedProcessLauncher
{
    private readonly List<IReadOnlyList<string>> _started = [];

    /// <summary>What was started, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    /// <summary>What starting raises, where a test scripts it.</summary>
    public Exception? Raises { get; init; }

    public void StartSelf(IReadOnlyList<string> arguments)
    {
        if (Raises is { } raised)
        {
            throw raised;
        }

        lock (_started)
        {
            _started.Add(arguments);
        }
    }
}
