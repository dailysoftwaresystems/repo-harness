using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The verdict vocabulary is a promise to whoever reads a gate: one word per outcome, one exit code
/// per word, and a fixed order when legs disagree. All three are pinned here, because a change to
/// any of them changes what a red build means without changing a single test that runs a leg.
/// </summary>
public sealed class LegVerdictTests
{
    [Fact]
    public void EveryVerdict_HasAName_AFailureFlag_AndAnExitCode()
    {
        foreach (var verdict in Enum.GetValues<LegVerdict>())
        {
            var info = Verdicts.Describe(verdict);

            Assert.False(string.IsNullOrWhiteSpace(info.Display), $"{verdict} has no display name");
            Assert.Equal(info.Display, info.Display.ToLowerInvariant());
            Assert.True(info.ExitCode >= 0, $"{verdict} has no exit code");
        }
    }

    [Fact]
    public void DisplayNames_AreTheOnesTheDocumentSpells()
    {
        // The table in docs/architecture.md, word for word. A verdict spelled two ways cannot be
        // grepped out of a gate's output.
        Assert.Equal("passed", Verdicts.Display(LegVerdict.Passed));
        Assert.Equal("failed", Verdicts.Display(LegVerdict.Failed));
        Assert.Equal("unwitnessed", Verdicts.Display(LegVerdict.Unwitnessed));
        Assert.Equal("inputs-moved", Verdicts.Display(LegVerdict.InputsMoved));
        Assert.Equal("unmeasured", Verdicts.Display(LegVerdict.Unmeasured));
        Assert.Equal("contended", Verdicts.Display(LegVerdict.Contended));
        Assert.Equal("skipped-not-selected", Verdicts.Display(LegVerdict.SkippedNotSelected));
        Assert.Equal("skipped-unavailable", Verdicts.Display(LegVerdict.SkippedUnavailable));
        Assert.Equal("skipped-tool-missing", Verdicts.Display(LegVerdict.SkippedToolMissing));
        Assert.Equal("refused-locked", Verdicts.Display(LegVerdict.RefusedLocked));
        Assert.Equal("log-held", Verdicts.Display(LegVerdict.LogHeld));
        Assert.Equal("poisoned", Verdicts.Display(LegVerdict.Poisoned));
    }

    [Fact]
    public void Precedence_IsTheOrderTheDocumentGives()
    {
        LegVerdict[] fundamentalFirst =
        [
            LegVerdict.Poisoned,
            LegVerdict.Unmeasured,
            LegVerdict.InputsMoved,
            LegVerdict.Contended,
            LegVerdict.LogHeld,
            LegVerdict.RefusedLocked,
            LegVerdict.Failed,
            LegVerdict.Unwitnessed,
        ];

        for (var index = 1; index < fundamentalFirst.Length; index++)
        {
            Assert.True(
                Verdicts.Rank(fundamentalFirst[index - 1]) < Verdicts.Rank(fundamentalFirst[index]),
                $"{fundamentalFirst[index - 1]} must outrank {fundamentalFirst[index]}");
        }
    }

    [Fact]
    public void Worst_ReportsTheMoreFundamentalVerdict()
    {
        // A leg whose inputs moved is not reported as failed even if its tests failed, because what
        // failed was a tree that never existed.
        Assert.Equal(LegVerdict.InputsMoved, Verdicts.Worst([LegVerdict.Passed, LegVerdict.Failed, LegVerdict.InputsMoved]));
        Assert.Equal(LegVerdict.Poisoned, Verdicts.Worst([LegVerdict.Unmeasured, LegVerdict.Poisoned]));
        Assert.Equal(LegVerdict.Failed, Verdicts.Worst([LegVerdict.Unwitnessed, LegVerdict.Failed, LegVerdict.Passed]));
        Assert.Equal(LegVerdict.Passed, Verdicts.Worst([LegVerdict.Passed, LegVerdict.SkippedNotSelected]));
    }

    [Fact]
    public void ExitCodes_AreTheContractCommandsPromise()
    {
        Assert.Equal(HarnessExit.Success, Verdicts.ExitCodeFor(LegVerdict.Passed));
        Assert.Equal(HarnessExit.CommandFailed, Verdicts.ExitCodeFor(LegVerdict.Failed));
        Assert.Equal(HarnessExit.Refused, Verdicts.ExitCodeFor(LegVerdict.RefusedLocked));
        Assert.Equal(HarnessExit.InternalError, Verdicts.ExitCodeFor(LegVerdict.Poisoned));

        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(LegVerdict.InputsMoved));
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(LegVerdict.Unmeasured));
        Assert.Equal(LegExit.Contended, Verdicts.ExitCodeFor(LegVerdict.Contended));
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(LegVerdict.Unwitnessed));
        Assert.Equal(LegExit.LogHeld, Verdicts.ExitCodeFor(LegVerdict.LogHeld));

        // A warning is not a failure: a switched-off machine is normal, and a command asked for
        // that leg by name decides for itself what to do about it, as `legs` does.
        Assert.Equal(HarnessExit.Success, Verdicts.ExitCodeFor(LegVerdict.SkippedUnavailable));
        Assert.Equal(HarnessExit.Success, Verdicts.ExitCodeFor(LegVerdict.SkippedToolMissing));
    }

    [Fact]
    public void LegCodes_StayInsideThePerCommandRange()
    {
        // 1-9 belongs to a command's own contract. A leg code outside it would collide with a
        // shared meaning, and a leg code shared with another command's would mean two things.
        Assert.InRange(LegExit.InputsMoved, 1, 9);
        Assert.InRange(LegExit.Contended, 1, 9);
        Assert.InRange(LegExit.Unwitnessed, 1, 9);
        Assert.InRange(LegExit.LogHeld, 1, 9);

        int[] codes = [LegExit.InputsMoved, LegExit.Contended, LegExit.Unwitnessed, LegExit.LogHeld];
        Assert.Equal(codes.Length, codes.Distinct().Count());

        foreach (var code in codes)
        {
            Assert.Null(HarnessExit.Describe(code));
        }
    }

    [Fact]
    public void FailureFlags_MatchTheDocument()
    {
        foreach (var verdict in new[]
                 {
                     LegVerdict.Failed, LegVerdict.Unwitnessed, LegVerdict.InputsMoved, LegVerdict.Unmeasured,
                     LegVerdict.Contended, LegVerdict.RefusedLocked, LegVerdict.LogHeld, LegVerdict.Poisoned,
                 })
        {
            Assert.True(Verdicts.IsFailure(verdict), $"{verdict} counts as a failure");
        }

        foreach (var verdict in new[]
                 {
                     LegVerdict.Passed, LegVerdict.SkippedNotSelected, LegVerdict.SkippedUnavailable, LegVerdict.SkippedToolMissing,
                 })
        {
            Assert.False(Verdicts.IsFailure(verdict), $"{verdict} does not count as a failure");
        }
    }

    [Fact]
    public void ALegWithNoVerdict_IsPoisonedAndSaysWhich()
    {
        var reached = ReachedVerdict.OrPoisoned(null, "win-msvc-release");

        Assert.Equal(LegVerdict.Poisoned, reached.Verdict);
        Assert.Contains("win-msvc-release", reached.Detail, StringComparison.Ordinal);
        Assert.True(Verdicts.IsFailure(reached.Verdict), "a leg that vanishes must fail the run");
    }

    [Fact]
    public void ARefusal_KeepsTheMeaningItWasRaisedWith()
    {
        // A host that is switched off is not a defect in the harness, and reporting it as poisoned
        // would send the reader looking for one.
        Assert.Equal(LegVerdict.SkippedUnavailable, Verdicts.ForRefusal(HarnessExit.HostUnavailable));
        Assert.Equal(LegVerdict.SkippedToolMissing, Verdicts.ForRefusal(HarnessExit.ToolMissing));
        Assert.Equal(LegVerdict.RefusedLocked, Verdicts.ForRefusal(HarnessExit.Refused));
        Assert.Equal(LegVerdict.LogHeld, Verdicts.ForRefusal(LegExit.LogHeld));
        Assert.Equal(LegVerdict.Poisoned, Verdicts.ForRefusal(HarnessExit.InternalError));
    }
}
