using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
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
        Assert.Contains("without a ledger entry", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerWithNoLedger_NamesTheCodeTheHostExitedWith()
    {
        // The only thing the host did say. A copy with no configuration, a leg that cannot be
        // placed there and a tool that refused before it began all end this way, and without the
        // code every one of them reads as the same shrug about a host that answered fine.
        var hosts = new ScriptedHostCommands((_, command) => HostResults.Finished(command, 11));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "build", Leg(), "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.Contains("exited 11", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that refused the whole run - a configuration, a command line or a policy its copy cannot
    /// satisfy - refused this run: the same refusal on this machine stops the run, and read as a host
    /// that could not be reached it became a skipped leg, with its reason and fix left on the host.
    /// </summary>
    [Theory]
    [InlineData(HarnessExit.Refused)]
    [InlineData(HarnessExit.ConfigInvalid)]
    [InlineData(HarnessExit.UsageError)]
    public async Task AHostsRefusalOfTheRun_IsThisRunsRefusal_InTheHostsOwnWords(int code)
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For("run", "git does not ignore this action's 'artifacts/'. Nothing has run."));

            return HostResults.Finished(command, code);
        });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "run", Leg(), "/home/dev/repo", ["corpus"], TestContext.Current.CancellationToken));

        Assert.Equal(code, refusal.ExitCode);
        Assert.Equal(
            "wsl Example-Linux refused 'run' for leg 'wsl-debug': git does not ignore this action's 'artifacts/'. Nothing has run.",
            refusal.Message);
    }

    [Fact]
    public async Task AHostsRefusalThatSaidNothing_StillRefuses_NamingTheCode()
    {
        var hosts = new ScriptedHostCommands((_, command) => HostResults.Finished(command, HarnessExit.Refused));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "run", Leg(), "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.EndsWith($"it exited {HarnessExit.Refused} and said nothing more", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Any other end without a ledger is still no verdict about the code, and now says what the host
    /// said about it rather than only the code it exited with.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithNoLedger_QuotesWhatTheHostSaid()
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For("build", "no selected leg can run"));

            return HostResults.Finished(command, 1);
        });

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "build", Leg(), "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.EndsWith("exited 1 without a ledger entry for it, saying: no selected leg can run", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal runs over several lines - one for each program an action may not start - and the
    /// whole of it travels, from the failure line to the end, rather than the first line alone.
    /// </summary>
    [Fact]
    public async Task AHostsRefusalOverSeveralLines_TravelsWhole()
    {
        const string First = "  - line 4: 'gcc' is not declared under 'tools' and is not a path inside the repository (declared: 'dotnet').";
        const string Second = "  - line 5: 'ninja' is not declared under 'tools' and is not a path inside the repository (declared: 'dotnet').";

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke("run: corpus: resolving the action");
            command.OnErrorLine?.Invoke(FailureLine.For("run", "'actions/corpus/corpus.yml' names 2 program(s) that may not run:"));
            command.OnErrorLine?.Invoke(First);
            command.OnErrorLine?.Invoke(Second);

            return HostResults.Finished(command, HarnessExit.Refused);
        });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "run", Leg(), "/home/dev/repo", ["corpus"], TestContext.Current.CancellationToken));

        Assert.Equal(
            "wsl Example-Linux refused 'run' for leg 'wsl-debug': 'actions/corpus/corpus.yml' names 2 program(s) that may not run:"
            + Environment.NewLine + First
            + Environment.NewLine + Second,
            refusal.Message);
    }

    /// <summary>
    /// A failure line the command's own output carried - a run of this tool inside a test suite
    /// prints one - is not the command's failure: the last one is, with what follows it.
    /// </summary>
    [Fact]
    public async Task TheLastFailureLine_IsTheCommandsOwn()
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For("run", "an inner run's own failure, printed by a step"));
            command.OnErrorLine?.Invoke("the step's output goes on");
            command.OnErrorLine?.Invoke(FailureLine.For("run", "git does not ignore this action's 'artifacts/'. Nothing has run."));

            return HostResults.Finished(command, HarnessExit.Refused);
        });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "run", Leg(), "/home/dev/repo", ["corpus"], TestContext.Current.CancellationToken));

        Assert.Equal(
            "wsl Example-Linux refused 'run' for leg 'wsl-debug': git does not ignore this action's 'artifacts/'. Nothing has run.",
            refusal.Message);
    }

    /// <summary>
    /// The host's agent fails under its own name when it could not start the command at all, and
    /// what it said travels as the command's own failure would.
    /// </summary>
    [Fact]
    public async Task AFailureTheHostsAgentReported_IsQuoted()
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For(HostAgentProtocol.CommandName, "the directory '/home/dev/repo' does not exist"));

            return HostResults.Finished(command, HarnessExit.InternalError);
        });

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "build", Leg(), "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.EndsWith("saying: the directory '/home/dev/repo' does not exist", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressPrecedingTheLedger_IsNotReadAsPartOfIt()
    {
        // The host writes its progress to standard error under --json, so this end parses the whole
        // of standard output. Measured: a leg whose detail held a brace was reported as a host that
        // could not be reached, turning a real verdict into a connection failure.
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, Ledger("failed", "error: expected '}' before 'x'", 1.5, 1.0, tests: null));

            return HostResults.Finished(command, HarnessExit.CommandFailed);
        });

        var entry = await Runner(hosts).RunAsync(
            "test", Leg(), "/home/dev/repo", [], TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Failed, entry.Verdict);
        Assert.Contains("expected '}'", entry.Detail, StringComparison.Ordinal);
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
                ".dotnet/tools/dssharness"),
        };

        return new PlacedLeg(
            "wsl-debug",
            new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" },
            host,
            Project: null,
            new RepoHarness.Core.Build.VariantKey("x86_64", "gcc", "debug", null),
            TreeRoot: @"C:\src\repo",
            HostTreeRoot: "/home/dev/repo",
            "/home/dev/repo/build/x86_64-gcc-debug",
            new WslHostConfig { RepositoryPath = "/home/dev/repo" },
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
