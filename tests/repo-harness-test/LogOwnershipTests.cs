using System.Globalization;
using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// A run that cannot own its log path cannot keep the evidence for its own verdict. These pin that
/// a live owner is refused as <c>log-held</c>, distinct from a held lock, and that an owner whose
/// process has gone is reclaimed rather than waited for.
/// </summary>
public sealed class LogOwnershipTests
{
    [Fact]
    public async Task AFreeLogPath_IsClaimed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output);
        var runId = RunId.New();
        var directory = temp.Combine("runs", runId.Value);

        var claim = await ownership.ClaimAsync(directory, runId, TestContext.Current.CancellationToken);

        Assert.True(claim.Taken);
        Assert.Null(claim.Verdict());
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);

        // Beside the directory, not inside it: wiping a run directory must not quietly free a path
        // a live run still owns.
        Assert.True(File.Exists(LogOwnership.OwnerFile(directory)));
        Assert.EndsWith(LogOwnership.OwnerSuffix, LogOwnership.OwnerFile(directory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALogPathOwnedByALiveRun_IsHeld()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output);
        var directory = temp.Combine("runs", "shared");

        // Owned by a run of this very process, which is alive by definition.
        Write(directory, Environment.MachineName, Environment.ProcessId, ProcessStart(), "20250101-120000-deadbeef");

        var claim = await ownership.ClaimAsync(directory, RunId.New(), TestContext.Current.CancellationToken);

        Assert.False(claim.Taken);
        Assert.Equal(LegVerdict.LogHeld, claim.Verdict()!.Verdict);
        Assert.Equal(LegExit.LogHeld, Verdicts.ExitCodeFor(claim.Verdict()!.Verdict));
        Assert.Contains("20250101-120000-deadbeef", claim.Verdict()!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALogPathOwnedByARunThatHasGone_IsReclaimed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output);
        var directory = temp.Combine("runs", "abandoned");
        var runId = RunId.New();

        Write(directory, Environment.MachineName, int.MaxValue - 1, DateTimeOffset.UtcNow.AddHours(-2), "20250101-120000-deadbeef");

        var claim = await ownership.ClaimAsync(directory, runId, TestContext.Current.CancellationToken);

        Assert.True(claim.Taken);
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);
        Assert.Contains("Reclaimed", factory.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunGivesUpOnlyItsOwnLogPath()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output);
        var directory = temp.Combine("runs", "owned");
        var mine = RunId.New();
        var other = RunId.New();

        await ownership.ClaimAsync(directory, mine, TestContext.Current.CancellationToken);

        // Another run's release must leave the owner alone, or two runs end up writing one set of logs.
        await ownership.ReleaseAsync(directory, other, TestContext.Current.CancellationToken);
        Assert.Equal(mine.Value, ownership.Owner(directory)!.RunId);

        await ownership.ReleaseAsync(directory, mine, TestContext.Current.CancellationToken);
        Assert.Null(ownership.Owner(directory));
    }

    [Fact]
    public async Task ARunReclaimsItsOwnLogPath()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output);
        var directory = temp.Combine("runs", "again");
        var runId = RunId.New();

        await ownership.ClaimAsync(directory, runId, TestContext.Current.CancellationToken);
        var second = await ownership.ClaimAsync(directory, runId, TestContext.Current.CancellationToken);

        Assert.True(second.Taken);
    }

    private static DateTimeOffset ProcessStart()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        return new DateTimeOffset(current.StartTime).ToUniversalTime();
    }

    private static void Write(string logDirectory, string machine, int processId, DateTimeOffset startedUtc, string runId)
    {
        var file = LogOwnership.OwnerFile(logDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var stamp = startedUtc.ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllText(
            file,
            "{ \"machine\": \"" + machine + "\", \"processId\": " + processId.ToString(CultureInfo.InvariantCulture)
            + ", \"processStartedUtc\": \"" + stamp + "\", \"runId\": \"" + runId
            + "\", \"takenUtc\": \"" + stamp + "\" }");
    }
}
