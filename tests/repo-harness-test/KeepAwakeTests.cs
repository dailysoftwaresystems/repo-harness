using System.Diagnostics;
using System.Globalization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>How a machine is held awake while a leg's own work runs on it.</summary>
public sealed class KeepAwakeTests
{
    private static readonly string[] Directories = ["/opt/homebrew/bin"];

    /// <summary>A machine that declares no command is held with nothing, and nothing is started.</summary>
    [Fact]
    public async Task AMachineThatDeclaresNoCommand_IsHeldWithNothing()
    {
        var factory = new HarnessFactory();
        var processes = new HeldProcesses();

        await using (new KeepAwake(processes, factory.Output).Hold("test", "mac", new LocalHostConfig(), Directories, TestContext.Current.CancellationToken))
        {
            Assert.Empty(processes.Started);
        }
    }

    /// <summary>
    /// The command runs for exactly as long as the leg's work does: started with the process that
    /// runs the leg filled in, under the host's environment and finding what the survey found, and
    /// stopped when the work ends - never before.
    /// </summary>
    [Fact]
    public async Task TheCommand_RunsForAsLongAsTheLegsWork_FilledInWithTheProcessRunningIt()
    {
        var factory = new HarnessFactory();
        var processes = new HeldProcesses();
        var host = new SshHostConfig
        {
            RepositoryPath = "~/repo",
            KeepAwake = ["caffeinate", "-dimsu", "-w", "{pid}"],
            Env = { ["RH_HOST"] = "mac" },
        };

        var holding = new KeepAwake(processes, factory.Output).Hold("test", "mac", host, Directories, TestContext.Current.CancellationToken);
        var (request, stopping) = Assert.Single(processes.Started);

        Assert.Equal("caffeinate", request.FileName);
        Assert.Equal(["-dimsu", "-w", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], request.Arguments);
        Assert.Equal("mac", request.Environment["RH_HOST"]);
        Assert.Equal(Directories, request.AppendToPath);
        Assert.False(stopping.IsCancellationRequested);

        await holding.DisposeAsync();

        Assert.True(stopping.IsCancellationRequested);
        Assert.DoesNotContain("keepAwake", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A real command is stopped when the leg's work ends - the whole of it, not left holding the
    /// machine awake for a run that has finished - and one stopped that way is not said to have
    /// ended early.
    /// </summary>
    [Fact]
    public async Task ARealCommand_IsStoppedWhenTheLegsWorkEnds()
    {
        var factory = new HarnessFactory();
        var host = new LocalHostConfig
        {
            KeepAwake = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath, "60000"],
            Env = { [TestHost.ChildModeVariable] = "sleep" },
        };

        var holding = new KeepAwake(factory.ProcessRunner, factory.Output).Hold("test", "native", host, [], TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();

        await holding.DisposeAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"stopping took {clock.Elapsed}");
        Assert.DoesNotContain("keepAwake", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A command that cannot start is said, and fails nothing: the leg's work goes on, and a sleep
    /// it did not prevent is still seen by the clocks.
    /// </summary>
    [Fact]
    public async Task ACommandThatCannotStart_IsSaid_AndFailsNothing()
    {
        var factory = new HarnessFactory();
        var host = new LocalHostConfig { KeepAwake = ["rh-missing-awake-" + Guid.NewGuid().ToString("N")[..8]] };

        await using (new KeepAwake(factory.ProcessRunner, factory.Output).Hold("test", "native", host, [], TestContext.Current.CancellationToken))
        {
        }

        Assert.Contains("native: keepAwake could not start", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A command that ends while the leg's work is still running is said at once, with what it said:
    /// the machine is no longer held awake for the rest of the leg.
    /// </summary>
    [Fact]
    public async Task ACommandThatEndsBeforeTheLeg_IsSaid()
    {
        var factory = new HarnessFactory();
        var ended = new ProcessResult(3, string.Empty, "caffeinate: no such process", TimeSpan.Zero, TimedOut: false);
        var host = new LocalHostConfig { KeepAwake = ["caffeinate", "-w", "{pid}"] };

        await using (new KeepAwake(new EndedProcesses(ended), factory.Output).Hold("test", "native", host, [], TestContext.Current.CancellationToken))
        {
            Assert.Contains(
                "native: keepAwake ended before the leg did (exit 3): caffeinate: no such process",
                factory.StandardError.ToString(),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A command the stop cannot reach - a descendant running as root under sudo keeps its output
    /// open - is waited for a bounded time and said, and never holds a leg whose work is done.
    /// </summary>
    [Fact]
    public async Task ACommandTheStopCannotReach_IsSaid_AndDoesNotHoldTheLeg()
    {
        var factory = new HarnessFactory();
        var host = new LocalHostConfig { KeepAwake = ["sudo", "systemd-inhibit", "sleep", "infinity"] };
        var holding = new KeepAwake(new UnstoppableProcesses(), factory.Output, TimeSpan.FromMilliseconds(200))
            .Hold("test", "native", host, [], TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();

        await holding.DisposeAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"stopping took {clock.Elapsed}");
        Assert.Contains("native: keepAwake did not stop within", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Every program runs on whatever is asked of it, as one out of the stop's reach does.</summary>
    private sealed class UnstoppableProcesses : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
            throw new UnreachableException();
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>Every program ends at once, with <paramref name="result"/>.</summary>
    private sealed class EndedProcesses(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(result);

        public string? FindExecutable(string command) => command;
    }
}

/// <summary>
/// Starts nothing: every program runs until it is stopped, and each is recorded with the token
/// that stops it.
/// </summary>
internal sealed class HeldProcesses : IProcessRunner
{
    private readonly List<(ProcessRequest Request, CancellationToken Stopping)> _started = [];

    /// <summary>Every program started, in order, with what stops it.</summary>
    public IReadOnlyList<(ProcessRequest Request, CancellationToken Stopping)> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        lock (_started)
        {
            _started.Add((request, cancellationToken));
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    }

    public string? FindExecutable(string command) => command;
}
