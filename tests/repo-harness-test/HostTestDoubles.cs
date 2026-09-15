using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
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
        => Task.FromResult(ShellProbe);

    public Task<ProcessResult> ProbeDefaultWslDistributionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultWslDistribution());
}

/// <summary>Reports a fixed measurement for each host, and records which hosts were measured.</summary>
internal sealed class RecordingInspector(Func<HostId, HostReport> report) : IHostInspector
{
    private readonly List<HostId> _inspected = [];

    /// <summary>Every host measured, in order.</summary>
    public IReadOnlyList<HostId> Inspected
    {
        get
        {
            lock (_inspected)
            {
                return [.. _inspected];
            }
        }
    }

    public Task<HostReport> InspectAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken = default)
    {
        lock (_inspected)
        {
            _inspected.Add(host);
        }

        return Task.FromResult(report(host));
    }
}

/// <summary>Process results for scripted hosts.</summary>
internal static class HostResults
{
    public static ProcessResult Ok(string output) => new(0, output, string.Empty, TimeSpan.Zero, TimedOut: false);

    public static ProcessResult Failed(int exitCode, string error) => new(exitCode, string.Empty, error, TimeSpan.Zero, TimedOut: false);

    public static InvalidOperationException Unexpected(HostCommand command)
        => new($"The host was not expected to run '{command.Program} {string.Join(' ', command.Arguments)}'.");
}
