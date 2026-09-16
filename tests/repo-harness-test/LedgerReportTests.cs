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
        Assert.Contains("build took", slow.Detail, StringComparison.Ordinal);
        Assert.Contains("412 tests", slow.Detail, StringComparison.Ordinal);

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
        Assert.Contains("the clock stepped during test", line.Detail, StringComparison.Ordinal);
        Assert.Equal(LegVerdict.Passed, line.Verdict);
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

        using var document = JsonDocument.Parse(report.ToJson());
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

    private static LegEntry Entry(string leg, LegVerdict verdict, TimeSpan duration, string detail, params PhaseRecord[] phases) => new()
    {
        Leg = leg,
        Verdict = verdict,
        Duration = duration,
        Detail = detail,
        Phases = phases,
    };

    private static PhaseRecord Phase(string name, int seconds) => new(name, TimeSpan.FromSeconds(seconds), ClockStepped: false);

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
