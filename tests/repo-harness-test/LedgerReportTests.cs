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
    [InlineData(LegVerdict.Failed, false, "", "failed: 2 leg(s) reported")]
    [InlineData(LegVerdict.InputsMoved, false, "", "inputs-moved: 2 leg(s) reported")]
    [InlineData(LegVerdict.Poisoned, false, "", "poisoned: 2 leg(s) reported")]
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

    [Fact]
    public void ALegRunningFewerTestsThanItsSiblings_IsMarked()
    {
        // Legs running the same suite are meant to run the same tests. A leg reporting three where
        // its siblings report 412 is green on both the exit code and the success pattern; the count
        // is the only thing in the ledger that can see the difference.
        var report = LedgerReport.From(
        [
            Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(10), string.Empty) with { TestCount = 412 },
            Entry("linux-gcc-release", LegVerdict.Passed, TimeSpan.FromSeconds(11), string.Empty) with { TestCount = 412 },
            Entry("wsl-clang-asan", LegVerdict.Passed, TimeSpan.FromSeconds(9), string.Empty) with { TestCount = 3 },
        ],
        durationWarningFactor: 0);

        var marked = report.Lines.Single(line => line.Leg == "wsl-clang-asan");

        Assert.True(marked.TimingsSuspect);
        Assert.Contains(marked.TimingNotes, note => note.Contains("ran 3 test(s)", StringComparison.Ordinal));
        Assert.All(
            report.Lines.Where(line => line.Leg != "wsl-clang-asan"),
            line => Assert.False(line.TimingsSuspect));
    }

    [Fact]
    public void TwoLegsDisagreeingAboutTheirCount_AreNotMarked()
    {
        // With two, "which one is wrong" has no answer, and marking both says nothing anyone can
        // act on.
        var report = LedgerReport.From(
        [
            Entry("win-msvc-release", LegVerdict.Passed, TimeSpan.FromSeconds(10), string.Empty) with { TestCount = 412 },
            Entry("wsl-clang-asan", LegVerdict.Passed, TimeSpan.FromSeconds(9), string.Empty) with { TestCount = 3 },
        ],
        durationWarningFactor: 0);

        Assert.All(report.Lines, line => Assert.False(line.TimingsSuspect));
    }
}
