using RepoHarness.Core.Anchors;
using RepoHarness.Core.Ci;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Git;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Results;
using RepoHarness.Core.Tools;


namespace RepoHarness.Tests;

/// <summary>
/// The exit-code contract is a promise to callers, and the parts of it that are
/// conventions rather than types are pinned here so they become build failures.
/// </summary>
public sealed class ExitCodeContractTests
{
    [Fact]
    public void SharedCodes_NeverIntrudeOnThePerCommandRange()
    {
        // Codes 1-9 belong to individual commands (see VerifyGitStatus). A shared
        // code landing there would silently collide with a command's own contract,
        // and HarnessExit.All is built by reflection, so a new constant joins the
        // shared set without anyone deciding it should.
        foreach (var description in HarnessExit.All)
        {
            Assert.True(
                description.Code == 0 || description.Code >= 10,
                $"{description.Name} = {description.Code} intrudes on the per-command range 1-9");
        }
    }

    [Fact]
    public void SharedCodes_AreUnique()
    {
        var duplicates = HarnessExit.All
            .GroupBy(description => description.Code)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(d => d.Name))}")
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EverySharedCode_IsExplained()
    {
        foreach (var description in HarnessExit.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(description.Explanation),
                $"{description.Name} has no explanation, so 'help exit-codes' shows a blank line");
        }
    }

    [Fact]
    public void SuccessIsZero_Everywhere()
    {
        Assert.Equal(0, HarnessExit.Success);
        Assert.Equal(0, (int)VerifyGitStatus.Success);
    }

    [Fact]
    public void PerCommandCodes_StayInsideTheirRange()
    {
        foreach (var status in Enum.GetValues<VerifyGitStatus>())
        {
            var value = (int)status;
            Assert.True(value is >= 0 and <= 9, $"{status} = {value} is outside the per-command range");
        }

        Assert.InRange(AnchorExit.Findings, 1, 9);
        Assert.InRange(LegsExit.Unavailable, 1, 9);
        Assert.InRange(ToolsExit.NotProvisioned, 1, 9);
        Assert.InRange(CiExit.LegRed, 1, 9);
        Assert.InRange(CiExit.MatrixDidNotRun, 1, 9);
        Assert.InRange(LegExit.InputsMoved, 1, 9);
        Assert.InRange(LegExit.Contended, 1, 9);
        Assert.InRange(LegExit.Unwitnessed, 1, 9);
        Assert.InRange(LegExit.LogHeld, 1, 9);
    }

    [Fact]
    public void EveryLegVerdict_MapsToACodeThatSaysWhatToDoNext()
    {
        // The whole reason the leg commands have a contract of their own: a reader who cannot tell
        // which of these fired cannot pick the remedy, and four of them call for four different ones.
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(LegVerdict.InputsMoved));
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(LegVerdict.Unmeasured));
        Assert.Equal(LegExit.Contended, Verdicts.ExitCodeFor(LegVerdict.Contended));
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(LegVerdict.Unwitnessed));
        Assert.Equal(LegExit.LogHeld, Verdicts.ExitCodeFor(LegVerdict.LogHeld));

        // And the three that reuse a shared code, because their remedy is the shared one.
        Assert.Equal(HarnessExit.CommandFailed, Verdicts.ExitCodeFor(LegVerdict.Failed));
        Assert.Equal(HarnessExit.Refused, Verdicts.ExitCodeFor(LegVerdict.RefusedLocked));
        Assert.Equal(HarnessExit.InternalError, Verdicts.ExitCodeFor(LegVerdict.Poisoned));
    }

    [Fact]
    public async Task ExitCodeTopic_DocumentsEveryPerCommandCode()
    {
        // The shared table is generated; these are prose, so nothing but a test keeps a new
        // per-command code from shipping undocumented.
        var result = await CliRunner.RunAsync(["help", "exit-codes"], TestContext.Current.CancellationToken);

        foreach (var expected in new[] { "install-missing-tools", "check-ci-legs", "build, test, run" })
        {
            Assert.Contains(expected, result.StandardOutput, StringComparison.Ordinal);
        }

        foreach (var verdict in new[] { "inputs-moved", "contended", "unwitnessed", "log-held" })
        {
            Assert.Contains(verdict, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Describe_FindsASharedCode_AndIgnoresACommandSpecificOne()
    {
        Assert.Equal(nameof(HarnessExit.Refused), HarnessExit.Describe(HarnessExit.Refused)?.Name);

        // 2 is verify-git's "not a repository"; it is not a shared code.
        Assert.Null(HarnessExit.Describe(2));
    }
}
