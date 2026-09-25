using System.Text.Json;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The ledger is what a gate is read from, so its columns, its arithmetic and its marks are pinned
/// here. The rule that outranks the rest: a timing mark never changes a verdict, because a host
/// that slept makes a duration meaningless and says nothing about whether the code passed.
/// </summary>
public sealed class LedgerReportTests
{
    /// <summary>
    /// A command stopped before any leg had a line answers with a ledger holding none - and one it
    /// was interrupted in says so, from the code it ends with, so a script never reads it as red.
    /// </summary>
    [Fact]
    public void AStoppedLedger_SaysWhetherItWasInterrupted()
    {
        using var interrupted = System.Text.Json.JsonDocument.Parse(LedgerReport.Stopped(HarnessExit.Cancelled, "Interrupted before completion."));
        using var refused = System.Text.Json.JsonDocument.Parse(LedgerReport.Stopped(HarnessExit.Refused, "refused"));

        Assert.True(interrupted.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.False(interrupted.RootElement.GetProperty("passed").GetBoolean());
        Assert.False(refused.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.Empty(refused.RootElement.GetProperty("legs").EnumerateArray());
    }

    /// <summary>
    /// The document names where the run keeps its records, and a leg another host ran names that
    /// host's own; a run that kept none, and a leg this machine ran, name nothing.
    /// </summary>
    [Fact]
    public void TheDocument_NamesWhereTheRecordsAre()
    {
        var report = LedgerReport.From(
        [
            Entry("here", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
            Entry("there", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty) with { RunDirectory = "/home/pi/repo/.harness-config/runs/r2" },
        ],
        durationWarningFactor: 0);

        using var ran = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: [], runDirectory: "/repo/.harness-config/runs/r1"));
        using var stopped = JsonDocument.Parse(report.ToJson(HarnessExit.Refused, "refused", runDirectory: "/repo/.harness-config/runs/r1"));
        using var none = JsonDocument.Parse(LedgerReport.Stopped(HarnessExit.Refused, "refused"));

        Assert.Equal("/repo/.harness-config/runs/r1", ran.RootElement.GetProperty("runDirectory").GetString());
        Assert.Equal("/repo/.harness-config/runs/r1", stopped.RootElement.GetProperty("runDirectory").GetString());
        Assert.False(none.RootElement.TryGetProperty("runDirectory", out _));

        var legs = ran.RootElement.GetProperty("legs").EnumerateArray().ToList();

        Assert.False(legs[0].TryGetProperty("runDirectory", out _));
        Assert.Equal("/home/pi/repo/.harness-config/runs/r2", legs[1].GetProperty("runDirectory").GetString());
    }

    /// <summary>
    /// Every leg's line names the compilers CMake configured its build with, beside whatever it
    /// said - a failure's reason included - and the document carries them as data; a leg that built
    /// nothing with CMake names none.
    /// </summary>
    [Fact]
    public void EveryLine_NamesTheCompilersItsBuildWasConfiguredWith()
    {
        IReadOnlyList<RepoHarness.Core.Build.CompilerFact> msvc = [new("C", "MSVC", "19.51.36231"), new("CXX", "MSVC", "19.51.36231")];

        var report = LedgerReport.From(
        [
            Entry("win", LegVerdict.Failed, TimeSpan.FromSeconds(1), "3 tests failed") with { Compilers = msvc },
            Entry("lin", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
        ],
        durationWarningFactor: 0);

        var rows = report.Render();

        Assert.EndsWith("3 tests failed; compiler: MSVC 19.51.36231 (C, CXX)", rows[1], StringComparison.Ordinal);
        Assert.DoesNotContain("compiler", rows[2], StringComparison.Ordinal);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var legs = document.RootElement.GetProperty("legs").EnumerateArray().ToList();

        Assert.Equal(
            ["C MSVC 19.51.36231", "CXX MSVC 19.51.36231"],
            legs[0].GetProperty("compilers").EnumerateArray().Select(compiler =>
                $"{compiler.GetProperty("language").GetString()} {compiler.GetProperty("id").GetString()} {compiler.GetProperty("version").GetString()}"));
        Assert.False(legs[1].TryGetProperty("compilers", out _));
    }

    /// <summary>
    /// Every leg's line names the developer environment its processes started in, after the compilers
    /// and whatever the leg said, and the document carries it as data; a leg that needed none names none.
    /// </summary>
    [Fact]
    public void EveryLine_NamesTheDeveloperEnvironmentItStartedIn()
    {
        var visualStudio = new RepoHarness.Core.Hosts.DeveloperEnvironmentFact("vs", @"C:\VS", "18.0.1", "14.50.35717", "amd64");

        var report = LedgerReport.From(
        [
            Entry("win", LegVerdict.Failed, TimeSpan.FromSeconds(1), "3 tests failed") with
            {
                Compilers = [new("C", "MSVC", "19.51.36231")],
                DeveloperEnvironment = visualStudio,
            },
            Entry("lin", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
        ],
        durationWarningFactor: 0);

        var rows = report.Render();

        Assert.EndsWith(
            "3 tests failed; compiler: MSVC 19.51.36231 (C); developer environment: vs (Visual Studio 18.0.1, MSVC 14.50.35717, amd64)",
            rows[1],
            StringComparison.Ordinal);
        Assert.DoesNotContain("developer environment", rows[2], StringComparison.Ordinal);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var legs = document.RootElement.GetProperty("legs").EnumerateArray().ToList();
        var environment = legs[0].GetProperty("developerEnvironment");

        Assert.Equal(
            ["vs", @"C:\VS", "18.0.1", "14.50.35717", "amd64"],
            new[] { "name", "installationPath", "installationVersion", "toolsVersion", "architecture" }.Select(name => environment.GetProperty(name).GetString()));
        Assert.False(legs[1].TryGetProperty("developerEnvironment", out _));
    }

    /// <summary>The line said the moment a leg reaches its verdict names the developer environment too.</summary>
    [Fact]
    public void TheVerdictsOwnLine_NamesTheDeveloperEnvironment()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "build");

        ledger.Record(Entry("win", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty) with
        {
            DeveloperEnvironment = new("vs", @"C:\VS", "18.0.1", "14.50.35717", "amd64"),
        });

        Assert.Contains(
            "build: win: passed (developer environment: vs (Visual Studio 18.0.1, MSVC 14.50.35717, amd64))",
            factory.StandardOutput.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>The line said the moment a leg reaches its verdict names them too.</summary>
    [Fact]
    public void TheVerdictsOwnLine_NamesTheCompilers()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "build");

        ledger.Record(Entry("win", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty) with { Compilers = [new("C", "GNU", "13.2.0")] });

        Assert.Contains("build: win: passed (compiler: GNU 13.2.0 (C))", factory.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A step a leg's operating system does not run is named on that leg's line, in the order the
    /// file declares it, so it is left out on purpose rather than simply absent; a leg that ran
    /// every step names none.
    /// </summary>
    [Fact]
    public void TheDocument_NamesTheStepsALegsSystemLeftOut()
    {
        var report = LedgerReport.From(
        [
            Entry("win", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
            Entry("lin", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty) with { SkippedSteps = ["msvc", "sign"] },
        ],
        durationWarningFactor: 0);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var legs = document.RootElement.GetProperty("legs").EnumerateArray().ToList();

        Assert.False(legs[0].TryGetProperty("skippedSteps", out _));
        Assert.Equal(["msvc", "sign"], legs[1].GetProperty("skippedSteps").EnumerateArray().Select(step => step.GetString()));
    }

    /// <summary>
    /// A leg's line names the manual steps it ran and the steps the run did not select, each in the order
    /// declared: a plain run lists the manual steps it left out, so it is never read as having run them,
    /// and a line that ran one says so. A leg whose run left nothing out and ran nothing manual names none.
    /// </summary>
    [Fact]
    public void TheDocument_NamesTheManualStepsRun_AndTheStepsLeftOut()
    {
        var report = LedgerReport.From(
        [
            Entry("win", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
            Entry("lin", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty) with { UnselectedSteps = ["bench"] },
            Entry("mac", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty) with { RanSteps = ["prepare", "bench"], ManualSteps = ["bench"], UnselectedSteps = ["build"] },
        ],
        durationWarningFactor: 0);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var legs = document.RootElement.GetProperty("legs").EnumerateArray().ToList();

        Assert.False(legs[0].TryGetProperty("ranSteps", out _));
        Assert.False(legs[0].TryGetProperty("manualSteps", out _));
        Assert.False(legs[0].TryGetProperty("unselectedSteps", out _));

        Assert.False(legs[1].TryGetProperty("manualSteps", out _));
        Assert.Equal(["bench"], legs[1].GetProperty("unselectedSteps").EnumerateArray().Select(step => step.GetString()));

        Assert.Equal(["prepare", "bench"], legs[2].GetProperty("ranSteps").EnumerateArray().Select(step => step.GetString()));
        Assert.Equal(["bench"], legs[2].GetProperty("manualSteps").EnumerateArray().Select(step => step.GetString()));
        Assert.Equal(["build"], legs[2].GetProperty("unselectedSteps").EnumerateArray().Select(step => step.GetString()));
    }

    /// <summary>
    /// A leg's line names each file its steps kept, as sync --pull takes it; a leg that kept nothing names none.
    /// </summary>
    [Fact]
    public void TheDocument_NamesWhatEachLegKept()
    {
        var report = LedgerReport.From(
        [
            Entry("win", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
            Entry("lin", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty) with { KeptOutputs = ["a/artifacts/r/lin/pack/payload.txt"] },
        ],
        durationWarningFactor: 0);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var legs = document.RootElement.GetProperty("legs").EnumerateArray().ToList();

        Assert.False(legs[0].TryGetProperty("keptOutputs", out _));
        Assert.Equal(["a/artifacts/r/lin/pack/payload.txt"], legs[1].GetProperty("keptOutputs").EnumerateArray().Select(path => path.GetString()));
    }

    [Fact]
    public void TheTable_HasTheFourColumnsInOrder()
    {
        var report = LedgerReport.From(
        [
            Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(134), "412 tests"),
            Entry("wsl-clang-asan", LegVerdict.Failed, TimeSpan.FromSeconds(362), "3 of 412 tests failed"),
            Entry("vps-arm64-gcc-rel", LegVerdict.SkippedUnavailable, TimeSpan.Zero, "ssh vps: ssh could not connect"),
        ],
        durationWarningFactor: 0);

        var rows = report.Render();

        Assert.Equal(["LEG", "VERDICT", "DURATION", "DETAIL"], LedgerReport.Headings);
        Assert.StartsWith("LEG", rows[0], StringComparison.Ordinal);
        Assert.Contains("VERDICT", rows[0], StringComparison.Ordinal);
        Assert.Contains("DURATION", rows[0], StringComparison.Ordinal);
        Assert.EndsWith("DETAIL", rows[0], StringComparison.Ordinal);

        Assert.Contains("win-msvc-release", rows[1], StringComparison.Ordinal);
        Assert.Contains("passed", rows[1], StringComparison.Ordinal);
        Assert.Contains("2m14s", rows[1], StringComparison.Ordinal);
        Assert.EndsWith("412 tests", rows[1], StringComparison.Ordinal);

        // A leg that never ran has no duration, and the column is left blank rather than showing 0s.
        Assert.Contains("skipped-unavailable", rows[3], StringComparison.Ordinal);
        Assert.DoesNotContain("0s", rows[3], StringComparison.Ordinal);

        // Every row lines up: the verdict starts at the same column on each.
        var verdictColumn = rows[0].IndexOf("VERDICT", StringComparison.Ordinal);
        Assert.Equal(verdictColumn, rows[1].IndexOf("passed", StringComparison.Ordinal));
        Assert.Equal(verdictColumn, rows[2].IndexOf("failed", StringComparison.Ordinal));
        Assert.Equal(verdictColumn, rows[3].IndexOf("skipped-unavailable", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(134, "2m14s")]
    [InlineData(362, "6m02s")]
    [InlineData(760, "12m40s")]
    [InlineData(42, "42s")]
    [InlineData(3840, "1h04m")]
    [InlineData(0, "")]
    public void Durations_AreWrittenAsTheTableWritesThem(int seconds, string expected)
        => Assert.Equal(expected, LedgerReport.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void APhaseSlowerThanItsSiblings_IsMarkedSuspect_WithoutChangingItsVerdict()
    {
        var report = LedgerReport.From(
        [
            Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(60), "412 tests", Phase("build", 60)),
            Entry("b", LegVerdict.Passed, TimeSpan.FromSeconds(62), "412 tests", Phase("build", 62)),
            Entry("slow", LegVerdict.Passed, TimeSpan.FromSeconds(400), "412 tests", Phase("build", 400)),
        ],
        durationWarningFactor: 3.0);

        var slow = report.Lines.Single(line => line.Leg == "slow");

        Assert.True(slow.TimingsSuspect);

        // The note travels beside the detail, not inside it, so a ledger reported onward composes
        // the mark once rather than once per machine it passed through.
        Assert.Contains(slow.TimingNotes, note => note.Contains("build took", StringComparison.Ordinal));
        Assert.Equal("412 tests", slow.Detail);

        // And the reader still sees both, in the one column they look at.
        var row = Assert.Single(report.Render(), line => line.StartsWith("slow", StringComparison.Ordinal));

        Assert.Contains("build took", row, StringComparison.Ordinal);
        Assert.Contains("412 tests", row, StringComparison.Ordinal);

        // The mark is about the clock, not about the code.
        Assert.Equal(LegVerdict.Passed, slow.Verdict);
        Assert.True(report.Passed);
        Assert.Equal(HarnessExit.Success, report.ExitCode);

        Assert.False(report.Lines.Single(line => line.Leg == "a").TimingsSuspect);
    }

    [Fact]
    public void AnEmulatedLeg_IsNeverComparedWithANativeOne()
    {
        // Emulation is slower by a factor nobody chose, so comparing the two marks every emulated
        // leg suspect forever and the mark stops meaning anything.
        var report = LedgerReport.From(
        [
            Entry("native", LegVerdict.Passed, TimeSpan.FromSeconds(60), string.Empty, Phase("test", 60)),
            Entry("emulated", LegVerdict.Passed, TimeSpan.FromSeconds(900), string.Empty, Phase("test", 900)) with { Emulated = true },
        ],
        durationWarningFactor: 3.0);

        Assert.All(report.Lines, line => Assert.False(line.TimingsSuspect));
    }

    [Fact]
    public void AFactorOfZero_MarksNothing()
    {
        var report = LedgerReport.From(
        [
            Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty, Phase("build", 1)),
            Entry("slow", LegVerdict.Passed, TimeSpan.FromSeconds(500), string.Empty, Phase("build", 500)),
        ],
        durationWarningFactor: 0);

        Assert.All(report.Lines, line => Assert.False(line.TimingsSuspect));
    }

    [Fact]
    public void APhaseThatSpannedAClockStep_SaysSo()
    {
        var report = LedgerReport.From(
        [
            Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(60), "412 tests", new PhaseRecord("test", TimeSpan.FromSeconds(60), ClockStepped: true)),
        ],
        durationWarningFactor: 3.0);

        var line = report.Lines.Single();

        Assert.True(line.TimingsSuspect);
        Assert.Contains("the clock stepped during test", line.TimingNotes, StringComparer.Ordinal);
        Assert.Equal("412 tests", line.Detail);
        Assert.Equal(LegVerdict.Passed, line.Verdict);

        Assert.Contains(
            report.Render(),
            row => row.Contains("the clock stepped during test", StringComparison.Ordinal));
    }

    /// <summary>
    /// A ledger is reported onward: every remote leg runs this same code on its own host, answers
    /// with --json, and the asking machine builds its own ledger from those lines. Measured on a
    /// consumer's two-leg run, the whole explanation arrived twice, concatenated, in the one column
    /// a reader actually looks at.
    /// </summary>
    [Fact]
    public void ALedgerReportedOnward_ComposesTheMarkOnce_NotOncePerMachine()
    {
        var first = LedgerReport.From(
            [Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(60), "412 tests", new PhaseRecord("test", TimeSpan.FromSeconds(60), ClockStepped: true))],
            durationWarningFactor: 3.0);

        var arrived = first.Lines.Single();

        // Exactly what a host's --json hands back, and what the asking machine makes of it.
        var again = LedgerReport.From(
            [
                new LegEntry
                {
                    Leg = arrived.Leg,
                    Verdict = arrived.Verdict,
                    Detail = arrived.Detail,
                    Duration = arrived.Duration,
                    CommandTime = arrived.CommandTime,
                    TestCount = arrived.TestCount,
                    TimingNotes = arrived.TimingNotes,
                },
            ],
            durationWarningFactor: 3.0);

        var row = Assert.Single(again.Render(), line => line.StartsWith("a ", StringComparison.Ordinal));
        var mark = "timings suspect:";

        Assert.Equal(row.IndexOf(mark, StringComparison.Ordinal), row.LastIndexOf(mark, StringComparison.Ordinal));
    }

    [Fact]
    public void CommandTimeAndHarnessOverhead_AreReportedApart()
    {
        var report = LedgerReport.From(
        [
            Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(100), string.Empty) with { CommandTime = TimeSpan.FromSeconds(70) },
        ],
        durationWarningFactor: 0);

        var line = report.Lines.Single();

        // A leg that looks slow because of sync, fingerprints and sampling is a different problem
        // from one whose build is slow.
        Assert.Equal(TimeSpan.FromSeconds(70), line.CommandTime);
        Assert.Equal(TimeSpan.FromSeconds(30), line.Overhead);
    }

    [Fact]
    public void TheRunsVerdict_IsTheMostFundamentalOneAnyLegReached()
    {
        var report = LedgerReport.From(
        [
            Entry("a", LegVerdict.Failed, TimeSpan.FromSeconds(1), "3 of 412 tests failed"),
            Entry("b", LegVerdict.InputsMoved, TimeSpan.FromSeconds(1), "2 inputs changed"),
            Entry("c", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
        ],
        durationWarningFactor: 0);

        Assert.Equal(LegVerdict.InputsMoved, report.Verdict);
        Assert.Equal(LegExit.InputsMoved, report.ExitCode);
        Assert.False(report.Passed);
    }

    [Fact]
    public void TheJsonLedger_CarriesTheSameFacts()
    {
        var report = LedgerReport.From(
        [
            Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(134), "412 tests") with { TestCount = 412 },
            Entry("lin-gcc-release", LegVerdict.InputsMoved, TimeSpan.FromSeconds(231), "2 inputs changed"),
        ],
        durationWarningFactor: 0);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var root = document.RootElement;

        Assert.Equal("inputs-moved", root.GetProperty("verdict").GetString());
        Assert.Equal(LegExit.InputsMoved, root.GetProperty("exitCode").GetInt32());
        Assert.False(root.GetProperty("passed").GetBoolean());

        var legs = root.GetProperty("legs").EnumerateArray().ToList();
        Assert.Equal(2, legs.Count);
        Assert.Equal("win-msvc-release", legs[0].GetProperty("leg").GetString());
        Assert.Equal("passed", legs[0].GetProperty("verdict").GetString());
        Assert.Equal(412, legs[0].GetProperty("testCount").GetInt32());
        Assert.Equal(134d, legs[0].GetProperty("durationSeconds").GetDouble());
        Assert.True(legs[1].GetProperty("failure").GetBoolean());
    }

    [Fact]
    public void ALegRecordedTwice_IsADefect()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");

        ledger.Record(Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty));

        // One leg, one verdict. A second line for the same leg would make the table say two things.
        Assert.Throws<InvalidOperationException>(() => ledger.Record(Entry("a", LegVerdict.Failed, TimeSpan.FromSeconds(1), string.Empty)));
    }

    [Fact]
    public void TheLedgerReportsOneLinePerTransition()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");

        ledger.Transition("win-msvc-release", "building");
        ledger.Record(Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(1), "412 tests"));

        var lines = factory.StandardOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("test: win-msvc-release: ", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// The line says how the run ended in every case where the code does not, so a FAIL line can
    /// never be the three characters after it and nothing else. Asserted over every shape a run
    /// can end in rather than over one, because a message composed per branch is exactly how one
    /// branch came to compose none.
    /// </summary>
    [Theory]
    [InlineData(LegVerdict.Failed, false, "", "failed: 1 of 2 leg(s); 1 passed")]
    [InlineData(LegVerdict.InputsMoved, false, "", "inputs-moved: 1 of 2 leg(s); 1 passed")]
    [InlineData(LegVerdict.Poisoned, false, "", "poisoned: 1 of 2 leg(s); 1 passed")]
    [InlineData(LegVerdict.SkippedToolMissing, false, "", "1 of 2 leg(s) passed; 1 did no work: b")]
    [InlineData(LegVerdict.SkippedUnavailable, false, "", "1 of 2 leg(s) passed; 1 did no work: b")]
    [InlineData(LegVerdict.Passed, false, "c", "2 of 2 leg(s) passed; 1 did no work: c")]
    [InlineData(LegVerdict.Passed, true, "", "interrupted after 2 leg(s)")]
    [InlineData(LegVerdict.Passed, false, "", "2 leg(s) passed")]
    public void Summarize_SaysHowTheRunEnded_WhateverItsShape(
        LegVerdict second,
        bool cancelled,
        string unfinished,
        string expected)
    {
        var report = LedgerReport.From(
            [
                Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
                Entry("b", second, TimeSpan.FromSeconds(1), string.Empty),
            ],
            durationWarningFactor: 0);

        IReadOnlyList<string> left = unfinished.Length == 0 ? [] : [unfinished];

        Assert.Equal(expected, report.Summarize(cancelled, left));

        // And a run that did not succeed always has something to say for itself.
        if (report.ExitCodeGiven(cancelled, left) != HarnessExit.Success)
        {
            Assert.NotEqual(string.Empty, report.Summarize(cancelled, left));
        }
    }

    /// <summary>
    /// The line a consumer's eight-leg gate ended on said "poisoned: 8 leg(s) reported" - read as eight
    /// poisoned legs, when two were, three had failed and three had passed. The run's verdict comes
    /// with the count that reached it, and the rest follow, worst first.
    /// </summary>
    [Fact]
    public void Summarize_CountsTheWorstVerdictsOwnLegs_AndNamesTheRest()
    {
        var report = LedgerReport.From(
            [
                Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
                Entry("b", LegVerdict.Failed, TimeSpan.FromSeconds(1), string.Empty),
                Entry("c", LegVerdict.Poisoned, TimeSpan.FromSeconds(1), string.Empty),
                Entry("d", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
                Entry("e", LegVerdict.Failed, TimeSpan.FromSeconds(1), string.Empty),
                Entry("f", LegVerdict.Poisoned, TimeSpan.FromSeconds(1), string.Empty),
                Entry("g", LegVerdict.Failed, TimeSpan.FromSeconds(1), string.Empty),
                Entry("h", LegVerdict.Passed, TimeSpan.FromSeconds(1), string.Empty),
            ],
            durationWarningFactor: 0);

        Assert.Equal("poisoned: 2 of 8 leg(s); 3 failed, 3 passed", report.Summarize(cancelled: false, []));
    }

    private static LegEntry Entry(string leg, LegVerdict verdict, TimeSpan duration, string detail, params PhaseRecord[] phases) => new()
    {
        Leg = leg,
        Verdict = verdict,
        Duration = duration,
        Detail = detail,
        Phases = phases,
    };

    private static PhaseRecord Phase(string name, int seconds) => new(name, TimeSpan.FromSeconds(seconds), ClockStepped: false);

    /// <summary>
    /// A leg that did no work is not a leg that passed. The verdict table already ranks a skip
    /// above a pass so that a run carrying one summarises as the warning; the exit code has to
    /// agree, or a gate reading it is told eight legs passed when none of them ran.
    /// </summary>
    [Fact]
    public void ARunWhereALegReachedNoVerdict_IsIncomplete_NotSuccess()
    {
        var report = LedgerReport.From(
        [
            Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(134), "412 tests"),
            Entry("wsl-clang-asan", LegVerdict.SkippedUnavailable, TimeSpan.Zero, "wsl Ubuntu could not be reached"),
        ],
        durationWarningFactor: 0);

        Assert.True(report.Passed, "nothing failed, so this is not a red run");
        Assert.False(report.Complete);
        Assert.Equal(HarnessExit.Incomplete, report.ExitCode);
        Assert.Equal(1, report.Reported);
        Assert.Equal("wsl-clang-asan", Assert.Single(report.WithoutVerdict).Leg);
    }

    [Fact]
    public void ARunWhereEveryLegWasSkipped_IsNeverSuccess()
    {
        var report = LedgerReport.From(
        [
            Entry("a", LegVerdict.SkippedUnavailable, TimeSpan.Zero, "no host"),
            Entry("b", LegVerdict.SkippedToolMissing, TimeSpan.Zero, "cmake is missing"),
        ],
        durationWarningFactor: 0);

        Assert.Equal(HarnessExit.Incomplete, report.ExitCode);
        Assert.Equal(0, report.Reported);
    }

    /// <summary>
    /// An interrupted run is built from the legs that finished, so left to itself the ledger would
    /// describe a run stopped after its first leg as a clean pass of one leg — while the process
    /// exited 130. A script reading the document instead of the shell's status would take that for
    /// a green run.
    /// </summary>
    [Fact]
    public void TheJsonLedger_SaysARunWasInterrupted_RatherThanReportingThePartAsAWhole()
    {
        var report = LedgerReport.From(
            [Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(10), "412 tests")],
            durationWarningFactor: 0);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: true, unfinished: ["b"]));
        var root = document.RootElement;

        Assert.Equal(HarnessExit.Cancelled, root.GetProperty("exitCode").GetInt32());
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.True(root.GetProperty("cancelled").GetBoolean());
        Assert.Equal("b", Assert.Single(root.GetProperty("unfinished").EnumerateArray()).GetString());
    }

    /// <summary>
    /// A leg still running when the rest finished is the same fact by another route: the run
    /// reported on fewer legs than it was asked about, whatever its rows say.
    /// </summary>
    [Fact]
    public void TheJsonLedger_IsNotComplete_WhenALegWasLeftUnfinished()
    {
        var report = LedgerReport.From(
            [Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(10), "412 tests")],
            durationWarningFactor: 0);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: ["b"]));

        Assert.False(document.RootElement.GetProperty("complete").GetBoolean());
        Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void TheJsonLedger_OfAnUninterruptedRun_SaysSo()
    {
        var report = LedgerReport.From(
            [Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(10), "412 tests")],
            durationWarningFactor: 0);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var root = document.RootElement;

        Assert.Equal(HarnessExit.Success, root.GetProperty("exitCode").GetInt32());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("cancelled").GetBoolean());
        Assert.Empty(root.GetProperty("unfinished").EnumerateArray());
    }

    [Fact]
    public void AnAllGreenRun_IsUnchanged()
    {
        var report = LedgerReport.From(
        [
            Entry("a", LegVerdict.Passed, TimeSpan.FromSeconds(10), "412 tests"),
            Entry("b", LegVerdict.Passed, TimeSpan.FromSeconds(12), "412 tests"),
        ],
        durationWarningFactor: 0);

        Assert.True(report.Complete);
        Assert.Equal(HarnessExit.Success, report.ExitCode);
        Assert.Empty(report.WithoutVerdict);
    }

    /// <summary>
    /// A failure still decides the run. An incomplete result is what a run reports when nothing
    /// failed; where something did, that is the more fundamental fact and it keeps its own code.
    /// </summary>
    [Fact]
    public void AFailingLeg_StillDecidesTheRun_EvenBesideASkippedOne()
    {
        var report = LedgerReport.From(
        [
            Entry("a", LegVerdict.Failed, TimeSpan.FromSeconds(10), "3 tests failed"),
            Entry("b", LegVerdict.SkippedUnavailable, TimeSpan.Zero, "no host"),
        ],
        durationWarningFactor: 0);

        Assert.False(report.Passed);
        Assert.Equal(HarnessExit.CommandFailed, report.ExitCode);
    }

    /// <summary>
    /// What a phase reported about its own timing is shown, and shown with the leg and phase that
    /// reported it. Collected under <c>--time</c> and, before this, never printed anywhere: the
    /// flag extracted the marks and discarded them, so it had no observable effect at all.
    /// </summary>
    [Fact]
    public void TimingsAPhaseReported_AreShownBelowTheTable_NamingTheLegAndPhase()
    {
        var report = LedgerReport.From(
        [
            Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(134), "412 tests") with
            {
                Timings = [new TimingMark("build", "took 1.5s", "1.5"), new TimingMark("test", "took 12.25s", "12.25")],
            },
            Entry("wsl-clang-asan", LegVerdict.Passed, TimeSpan.FromSeconds(200), "412 tests") with
            {
                Timings = [new TimingMark("build", "took 9s", "9")],
            },
        ],
        durationWarningFactor: 0);

        var rows = report.Render();
        var timings = string.Join("\n", rows);

        Assert.Contains("TIMINGS", timings, StringComparison.Ordinal);
        Assert.Contains("win-msvc-release", timings, StringComparison.Ordinal);
        Assert.Contains("took 12.25s", timings, StringComparison.Ordinal);
        Assert.Contains("12.25", timings, StringComparison.Ordinal);

        // Each mark names the phase that printed it: a leg runs several, and a bare number with
        // nothing saying which phase produced it measures nothing.
        var line = Assert.Single(rows, row => row.Contains("took 9s", StringComparison.Ordinal));
        Assert.Contains("wsl-clang-asan", line, StringComparison.Ordinal);
        Assert.Contains("build", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without <c>--time</c>, or with it where nothing matched, the block is absent rather than
    /// printed empty: a heading over nothing reads as a run that reported no timings.
    /// </summary>
    [Fact]
    public void WithNoTimings_TheBlockIsAbsent()
    {
        var report = LedgerReport.From(
            [Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(134), "412 tests")],
            durationWarningFactor: 0);

        Assert.DoesNotContain("TIMINGS", string.Join("\n", report.Render()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg that did not pass shows the last lines its deciding phase printed, below the table and named
    /// by the leg, in the text and in the JSON alike; a leg that passed shows none, and the JSON leaves
    /// the field out for it.
    /// </summary>
    [Fact]
    public void TheLastLinesAFailedLegPrinted_AreShown_ForItAlone()
    {
        var report = LedgerReport.From(
        [
            Entry("mac-release", LegVerdict.Failed, TimeSpan.FromSeconds(80), "test exited 8") with
            {
                LogTail = ["1 test failed out of 412", "The following tests FAILED: 17 - git_state"],
            },
            Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(134), "412 tests"),
        ],
        durationWarningFactor: 0);

        var rows = report.Render();
        var heading = Assert.Single(rows, row => row.Contains("line(s) its phase printed", StringComparison.Ordinal));

        Assert.StartsWith("mac-release: the last 2 line(s)", heading, StringComparison.Ordinal);
        Assert.Equal("  | The following tests FAILED: 17 - git_state", rows[^1]);

        using var json = System.Text.Json.JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var legs = json.RootElement.GetProperty("legs");

        Assert.Equal(2, legs[0].GetProperty("logTail").GetArrayLength());
        Assert.False(legs[1].TryGetProperty("logTail", out var none) && none.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void TheJsonLedger_CarriesEveryTimingMark()
    {
        var report = LedgerReport.From(
        [
            Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(134), "412 tests") with
            {
                Timings = [new TimingMark("build", "took 1.5s", "1.5")],
            },
        ],
        durationWarningFactor: 0);

        var json = report.ToJson(cancelled: false, unfinished: []);

        Assert.Contains("\"build\"", json, StringComparison.Ordinal);
        Assert.Contains("took 1.5s", json, StringComparison.Ordinal);
        Assert.Contains("\"1.5\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Legs running the same suite are meant to run the same tests. A leg reporting three where its
    /// siblings report 412 is green on both the exit code and the success pattern; the count is the
    /// only thing in the ledger that can see the difference, and it is marked as that - never as a
    /// timing, since a count says nothing about the clock.
    /// </summary>
    [Fact]
    public void ALegRunningFewerTestsThanItsSiblings_IsMarked_AsItsOwnNote_NeverATimingOne()
    {
        var report = LedgerReport.From(
        [
            Counted("win-msvc-release", 412),
            Counted("linux-gcc-release", 412),
            Counted("wsl-clang-asan", 3),
        ],
        durationWarningFactor: 0);

        var marked = report.Lines.Single(line => line.Leg == "wsl-clang-asan");

        Assert.True(marked.TestCountDiffers);
        Assert.Equal("it ran 3 test(s), where 2 other leg(s) ran 412", marked.TestCountNote);
        Assert.False(marked.TimingsSuspect);
        Assert.Empty(marked.TimingNotes);
        Assert.All(report.Lines.Where(line => line.Leg != "wsl-clang-asan"), line => Assert.False(line.TestCountDiffers));
    }

    /// <summary>With two, "which one is wrong" has no answer, and marking both says nothing anyone can act on.</summary>
    [Fact]
    public void TwoLegsDisagreeingAboutTheirCount_AreNotMarked()
    {
        var report = LedgerReport.From([Counted("win-msvc-release", 412), Counted("wsl-clang-asan", 3)], durationWarningFactor: 0);

        Assert.All(report.Lines, line => Assert.False(line.TestCountDiffers));
    }

    /// <summary>
    /// A leg of another project runs another suite, so its count is never held against this one's:
    /// pooled with them, a monorepo's small tool project was marked beside a large application.
    /// </summary>
    [Fact]
    public void LegsOfDifferentProjects_AreNeverComparedByTheirCounts()
    {
        var report = LedgerReport.From(
        [
            Counted("app-debug", 2237),
            Counted("app-release", 2237),
            Counted("app-asan", 2237),
            Counted("tools-debug", 5, project: "tools"),
        ],
        durationWarningFactor: 0);

        Assert.All(report.Lines, line => Assert.False(line.TestCountDiffers));
    }

    /// <summary>
    /// A leg whose test settings name a set of their own - a platform's own tests - is compared only
    /// with the legs naming that set, and the rest only with the project's shared set: a Windows-only
    /// test marks nothing, and a Windows leg that dropped tests from the Windows set still is. Names
    /// compare ignoring case, as configuration keys do.
    /// </summary>
    [Fact]
    public void ALegNamingItsOwnTestSet_IsComparedOnlyWithTheLegsNamingIt()
    {
        var report = LedgerReport.From(
        [
            Counted("linux-debug", 2237),
            Counted("linux-release", 2237),
            Counted("macos-debug", 2237, project: "App"),
            Counted("windows-debug", 2238, testSet: "windows"),
            Counted("windows-release", 2238, testSet: "Windows"),
            Counted("windows-asan", 5, testSet: "windows"),
        ],
        durationWarningFactor: 0);

        var marked = Assert.Single(report.Lines, line => line.TestCountDiffers);

        Assert.Equal("windows-asan", marked.Leg);
        Assert.Equal("it ran 5 test(s), where 2 other leg(s) ran 2238", marked.TestCountNote);
    }

    /// <summary>A count whose leg resolves no project belongs to no suite, and is compared with nothing.</summary>
    [Fact]
    public void ALegWithNoProject_IsComparedWithNothing()
    {
        var report = LedgerReport.From(
            [Counted("a", 412, project: null), Counted("b", 412, project: null), Counted("c", 3, project: null)],
            durationWarningFactor: 0);

        Assert.All(report.Lines, line => Assert.False(line.TestCountDiffers));
    }

    /// <summary>
    /// A count that stands apart is its own part of the leg's line, labelled as that, beside a timing
    /// mark rather than under it; and its own fields in --json, with what it belongs to, so a script
    /// never has to pick it out of the timing notes.
    /// </summary>
    [Fact]
    public void ACountThatStandsApart_IsItsOwnPartOfTheLine_AndItsOwnFieldsInJson()
    {
        var report = LedgerReport.From(
        [
            Counted("win-msvc-release", 412) with { Phases = [Phase("test", 10)] },
            Counted("linux-gcc-release", 412) with { Phases = [Phase("test", 10)] },
            Counted("wsl-clang-asan", 3, testSet: null) with { Phases = [Phase("test", 100)] },
        ],
        durationWarningFactor: 3.0);

        var row = Assert.Single(report.Render(), line => line.StartsWith("wsl-clang-asan", StringComparison.Ordinal));

        Assert.EndsWith(
            "3 tests; test count differs: it ran 3 test(s), where 2 other leg(s) ran 412; timings suspect: test took 1m40s against 10s on sibling legs",
            row,
            StringComparison.Ordinal);

        using var document = JsonDocument.Parse(report.ToJson(cancelled: false, unfinished: []));
        var legs = document.RootElement.GetProperty("legs").EnumerateArray().ToDictionary(leg => leg.GetProperty("leg").GetString()!);
        var marked = legs["wsl-clang-asan"];

        Assert.True(marked.GetProperty("testCountDiffers").GetBoolean());
        Assert.Equal("it ran 3 test(s), where 2 other leg(s) ran 412", marked.GetProperty("testCountNote").GetString());
        Assert.Equal("app", marked.GetProperty("project").GetString());
        Assert.False(marked.TryGetProperty("testSet", out _));
        Assert.Equal(["test took 1m40s against 10s on sibling legs"], marked.GetProperty("timingNotes").EnumerateArray().Select(note => note.GetString()));
        Assert.False(legs["win-msvc-release"].GetProperty("testCountDiffers").GetBoolean());
        Assert.False(legs["win-msvc-release"].TryGetProperty("testCountNote", out _));
    }

    /// <summary>A passing leg of <paramref name="project"/> that ran <paramref name="count"/> tests of <paramref name="testSet"/>.</summary>
    private static LegEntry Counted(string leg, int count, string? project = "app", string? testSet = null)
        => Entry(leg, LegVerdict.Passed, TimeSpan.FromSeconds(10), string.Empty) with { TestCount = count, Project = project, TestSet = testSet };
}
