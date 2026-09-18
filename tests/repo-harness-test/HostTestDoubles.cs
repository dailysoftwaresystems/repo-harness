using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;

namespace RepoHarness.Tests;

/// <summary>
/// Answers every command a host would receive from a script, and records what was asked. A real
/// WSL distribution or ssh host would make each rule depend on what one machine has installed.
/// </summary>
internal sealed class ScriptedHostCommands(Func<HostConnection, HostCommand, ProcessResult> respond) : IHostCommandRunner
{
    private readonly List<(HostConnection Connection, HostCommand Command)> _calls = [];
    private readonly List<HostConnection> _shellProbes = [];

    /// <summary>What the ssh shell probe answers; by default a shell that is not cmd.</summary>
    public ProcessResult ShellProbe { get; set; } = HostResults.Ok("%COMSPEC%\n");

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

        return Task.FromResult(respond(connection, command));
    }

    public Task<ProcessResult> ProbeShellAsync(HostConnection connection, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _shellProbes.Add(connection);
        }

        return Task.FromResult(ShellProbe);
    }

    public Task<ProcessResult> ProbeDefaultWslDistributionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultWslDistribution());
}

/// <summary>Reports a fixed measurement for each host, and records which hosts were measured, and for which emulators.</summary>
internal sealed class RecordingInspector(Func<HostId, HostReport> report) : IHostInspector
{
    private readonly List<(HostId Host, IReadOnlyDictionary<string, EmulatorConfig> Emulators, IReadOnlyList<string> Programs)> _inspected = [];

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
        IReadOnlyList<string> programs,
        CancellationToken cancellationToken = default)
    {
        lock (_inspected)
        {
            _inspected.Add((host, emulators, programs));
        }

        var answer = report(host);

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

    /// <summary>
    /// What the DssHarness on a host does with a run request: passes on what the command wrote to standard
    /// error, says it finished with <paramref name="exitCode"/>, and exits with that code.
    /// </summary>
    public static ProcessResult Finished(HostCommand command, int exitCode, string error = "")
    {
        var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions)
            ?? throw new InvalidOperationException("The host was sent no request.");

        var completion = HostAgentProtocol.CompletionLine(
            request.Nonce ?? throw new InvalidOperationException("The request carries no nonce."),
            exitCode);

        foreach (var line in error.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            command.OnErrorLine?.Invoke(line);
        }

        command.OnErrorLine?.Invoke(completion);

        return new ProcessResult(exitCode, string.Empty, error + completion + "\n", TimeSpan.Zero, TimedOut: false);
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
        platform.HomeDirectory.Returns(home ?? TestHost.TemporaryRoot);
        return platform;
    }

    /// <summary>A loader that hands every command <paramref name="config"/>, for a repository at <paramref name="root"/>.</summary>
    public static IHarnessContextLoader Loader(HarnessConfig config, string root)
    {
        var loader = Substitute.For<IHarnessContextLoader>();
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HarnessContext(new HarnessLayout(root, root), config)));
        return loader;
    }

    /// <summary>A leg that needs <paramref name="os"/> on <paramref name="processor"/>, built in the "debug" configuration.</summary>
    public static LegConfig Leg(string os, string processor) => new() { Os = os, Processor = processor, Config = "debug" };
}
