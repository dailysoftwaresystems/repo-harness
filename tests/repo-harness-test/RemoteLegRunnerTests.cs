using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;

namespace RepoHarness.Tests;

/// <summary>
/// A leg placed on another machine runs there. These pin the part that makes that true: the request
/// the host is sent, and how its answer becomes this run's ledger line.
/// </summary>
public sealed class RemoteLegRunnerTests
{
    [Fact]
    public async Task TheHostIsAskedToRunTheSameCommand_ForThatLegAlone_AndOnItself()
    {
        HostCommand? sent = null;

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            Answer(command, Ledger("passed", "412 tests", 2.5, 2.1, 412));

            return HostResults.Finished(command, 0);
        });

        var entry = await Runner(hosts).RunAsync(
            "test",
            Leg(),
            "/home/dev/repo",
            ["--filter", "auth"],
            TestContext.Current.CancellationToken);

        var request = Request(sent);

        Assert.Equal(HostAgentRequestKind.Run, request.Kind);
        Assert.Equal("/home/dev/repo", request.Directory);

        // The command this machine was asked to run, for this leg only, answered as data, and with
        // the host told to run it on itself: a host free to dispatch onward would put the verdict one
        // further hop from the reader, and could not terminate by construction.
        Assert.Equal(
            ["test", "--legs", "wsl-debug", "--json", RemoteLegRunner.HereOption, "--filter", "auth"],
            request.Arguments);

        Assert.Equal(LegVerdict.Passed, entry.Verdict);
        Assert.Equal("412 tests", entry.Detail);
        Assert.Equal(412, entry.TestCount);
        Assert.Equal(TimeSpan.FromSeconds(2.5), entry.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2.1), entry.CommandTime);
    }

    [Fact]
    public async Task TheVerdictTheHostReached_IsTheVerdictThisRunReports()
    {
        // Including the ones a local run could not produce: the host is the only machine that could
        // have seen its own tree move or its own build directory contended.
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, Ledger("inputs-moved", "2 inputs changed", 1, 1, null));

            return HostResults.Finished(command, LegExit.InputsMoved);
        });

        var entry = await Runner(hosts).RunAsync(
            "test", Leg(), "/home/dev/repo", [], TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.InputsMoved, entry.Verdict);
        Assert.Equal("2 inputs changed", entry.Detail);
    }

    [Fact]
    public async Task AHostThatNeverSaidHowItFinished_IsNeverReportedAsAFailedLeg()
    {
        // The command may not have run, or run only in part. A red verdict would blame the code for
        // a connection, which is the one thing a leg's verdict must never do.
        var hosts = new ScriptedHostCommands((_, _) => HostResults.Failed(255, "ssh: connection closed"));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "test", Leg(), "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.Contains("never reported how it finished", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerWithNoLedger_IsNotReadAsAPass()
    {
        var hosts = new ScriptedHostCommands((_, command) => HostResults.Finished(command, 0));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "build", Leg(), "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.Contains("reported no ledger entry", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatWasNeverReached_IsRefusedBeforeAnythingIsSent()
    {
        var hosts = new ScriptedHostCommands((_, _) =>
            throw new InvalidOperationException("Nothing should have been sent."));

        var unreachable = Leg() with
        {
            Host = new HostReport { Host = HostId.Wsl("Example-Linux"), Reason = "it did not answer" },
        };

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "test", unreachable, "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.Contains("it did not answer", failure.Message, StringComparison.Ordinal);
    }

    private static RemoteLegRunner Runner(ScriptedHostCommands hosts)
        => new(hosts, new HarnessFactory().Output);

    /// <summary>
    /// Writes a ledger where the host writes one: standard output. Standard error carries the
    /// protocol's completion line, so an answer written there would be read as a transport message.
    /// </summary>
    private static void Answer(HostCommand command, string ledger)
    {
        foreach (var line in ledger.Split('\n'))
        {
            command.OnOutputLine?.Invoke(line.TrimEnd('\r'));
        }
    }

    private static HostAgentRequest Request(HostCommand? sent)
    {
        Assert.NotNull(sent);

        return System.Text.Json.JsonSerializer.Deserialize<HostAgentRequest>(
            sent.StandardInput!,
            HostAgentProtocol.JsonOptions)!;
    }

    private static PlacedLeg Leg()
    {
        var host = new HostReport
        {
            Host = HostId.Wsl("Example-Linux"),
            Os = "linux",
            Processor = "x86_64",
            Session = new HostSession(
                new HostConnection { Host = HostId.Wsl("Example-Linux"), Distribution = "Example-Linux" },
                ".dotnet/tools/DssHarness"),
        };

        return new PlacedLeg(
            "wsl-debug",
            new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" },
            host,
            Project: null,
            new RepoHarness.Core.Build.VariantKey("x86_64", "gcc", "debug", null),
            "/home/dev/repo",
            "/home/dev/repo/build/x86_64-gcc-debug",
            Emulated: false);
    }

    private static string Ledger(string verdict, string detail, double duration, double command, int? tests)
        => $$"""
            {
              "verdict": "{{verdict}}",
              "exitCode": 0,
              "passed": true,
              "legs": [
                {
                  "leg": "wsl-debug",
                  "verdict": "{{verdict}}",
                  "failure": false,
                  "durationSeconds": {{duration}},
                  "commandSeconds": {{command}},
                  "overheadSeconds": 0,
                  "detail": "{{detail}}",
                  "timingsSuspect": false,
                  "timingNotes": [],
                  "testCount": {{(tests?.ToString() ?? "null")}}
                }
              ]
            }
            """;
}
