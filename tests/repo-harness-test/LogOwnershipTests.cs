using System.Globalization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;

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
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var runId = RunId.New();
        var directory = temp.Combine("runs", runId.Value);

        var claim = await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

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
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "shared");

        // Owned by a run of this very process, which is alive by definition.
        Write(directory, Environment.MachineName, Environment.ProcessId, ProcessStart(), "20250101-120000-deadbeef");

        var claim = await ownership.ClaimAsync(directory, RunId.New(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(claim.Taken);
        Assert.Equal(LegVerdict.LogHeld, claim.Verdict()!.Verdict);
        Assert.Equal(LegExit.LogHeld, Verdicts.ExitCodeFor(claim.Verdict()!.Verdict));
        Assert.Contains("20250101-120000-deadbeef", claim.Verdict()!.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The only way out of an owner this machine will not reclaim on its own. Without it a stuck
    /// <c>.owner.json</c> can only be deleted by hand, which is the kind of manual step this tool
    /// exists to remove.
    /// </summary>
    [Fact]
    public async Task ALogPathOwnedByALiveRun_CanBeTakenWhenItIsForced()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "stuck");
        var runId = RunId.New();

        Write(directory, Environment.MachineName, Environment.ProcessId, ProcessStart(), "20250101-120000-deadbeef");

        var claim = await ownership.ClaimAsync(directory, runId, force: true, TestContext.Current.CancellationToken);

        Assert.True(claim.Taken);
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);
        Assert.Contains("--force-lock was given", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same upgrade for a log path: an owner file written before the stamp existed can only be
    /// judged by whether anything carries its id, and says so rather than looking like a live run.
    /// </summary>
    [Fact]
    public async Task AnOwnerFromAnOlderBuild_IsHeldWhileItsIdIsCarried_AndSaysWhy()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "from-before");

        WriteLegacy(directory, Environment.MachineName, Environment.ProcessId);

        var claim = await ownership.ClaimAsync(directory, RunId.New(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(claim.Taken);
        Assert.Contains("recorded by an older build", claim.Verdict()!.Detail, StringComparison.Ordinal);

        // And the way out is the one the message names.
        var forced = await ownership.ClaimAsync(
            directory, RunId.New(), force: true, TestContext.Current.CancellationToken);

        Assert.True(forced.Taken);
    }

    [Fact]
    public async Task ALogPathOwnedByARunThatHasGone_IsReclaimed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "abandoned");
        var runId = RunId.New();

        Write(directory, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-deadbeef");

        var claim = await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(claim.Taken);
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);
        Assert.Contains("Reclaimed", factory.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunGivesUpOnlyItsOwnLogPath()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "owned");
        var mine = RunId.New();
        var other = RunId.New();

        await ownership.ClaimAsync(directory, mine, cancellationToken: TestContext.Current.CancellationToken);

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
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "again");
        var runId = RunId.New();

        await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);
        var second = await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(second.Taken);
    }

    /// <summary>This process's own stamp, which is what makes an owner written with it a live one.</summary>
    private static string? ProcessStart() => new ProcessIdentity(new HostPlatform()).Current;

    /// <summary>One owner file exactly as the release before the process stamp wrote it.</summary>
    private static void WriteLegacy(string logDirectory, string machine, int processId)
    {
        var file = LogOwnership.OwnerFile(logDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var stamp = DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllText(
            file,
            "{ \"machine\": \"" + machine + "\", \"processId\": " + processId.ToString(CultureInfo.InvariantCulture)
            + ", \"processStartedUtc\": \"" + stamp + "\", \"runId\": \"20250101-120000-deadbeef\""
            + ", \"takenUtc\": \"" + stamp + "\" }");
    }

    private static void Write(string logDirectory, string machine, int processId, string? processStamp, string runId)
    {
        var file = LogOwnership.OwnerFile(logDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var taken = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var stampField = processStamp is null ? string.Empty : "\"processStamp\": \"" + processStamp + "\", ";

        File.WriteAllText(
            file,
            "{ \"machine\": \"" + machine + "\", \"processId\": " + processId.ToString(CultureInfo.InvariantCulture)
            + ", " + stampField + "\"runId\": \"" + runId
            + "\", \"takenUtc\": \"" + taken + "\" }");
    }
}
