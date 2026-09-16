using System.Diagnostics;
using System.Globalization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The lock is what keeps two runs off one tree. A held lock refuses immediately and names its
/// holder, staleness is decided by liveness rather than by a timeout, and a lock is released by the
/// run that took it. Each of those is a different way for two runs to corrupt each other's results.
/// </summary>
public sealed class RunLockTests
{
    [Fact]
    public async Task AHeldLock_RefusesAtOnce_AndNamesItsHolder()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);
        var layout = Layout(temp);

        await using var held = await runLock.AcquireAsync(layout, Request(LockScope.TreeExclusive, "sync"), TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        var refusal = await Assert.ThrowsAsync<HarnessException>(() => runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeExclusive, "build"),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains(Environment.MachineName, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), refusal.Message, StringComparison.Ordinal);
        Assert.Contains(held.Entry.Holder.RunId, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("sync", refusal.Message, StringComparison.Ordinal);

        // It never waits: blocking for hours is worse than a refusal somebody can act on.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the refusal took {watch.Elapsed}");
    }

    [Fact]
    public async Task ARecycledProcessId_IsNotMistakenForALiveHolder()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);
        var layout = Layout(temp);

        // This process's own id, recorded with a start time it cannot have: exactly what a run sees
        // after the operating system has handed the id out again.
        Write(layout, Entry(Environment.MachineName, Environment.ProcessId, DateTimeOffset.UnixEpoch));

        await using var taken = await runLock.AcquireAsync(layout, Request(LockScope.TreeExclusive, "sync"), TestContext.Current.CancellationToken);

        Assert.Single(runLock.Read(layout));
        Assert.Equal(Environment.ProcessId, runLock.Read(layout).Single().Holder.ProcessId);
        Assert.Contains("Reclaimed", factory.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeadHolderOnThisMachine_IsReclaimedAndTheReclaimIsReported()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);
        var layout = Layout(temp);

        // An id no process on any platform carries.
        Write(layout, Entry(Environment.MachineName, int.MaxValue - 1, DateTimeOffset.UtcNow.AddHours(-1)));

        await using var taken = await runLock.AcquireAsync(layout, Request(LockScope.TreeExclusive, "sync"), TestContext.Current.CancellationToken);

        Assert.Single(runLock.Read(layout));
        Assert.Contains("Reclaimed", factory.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("no longer running", factory.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHolderOnAnotherMachine_StandsUntilItIsForced()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);
        var layout = Layout(temp);

        // Dead by every test this machine can apply, and still not reclaimed: nothing here can ask
        // that machine whether the run is still going.
        Write(layout, Entry("another-machine", int.MaxValue - 1, DateTimeOffset.UtcNow.AddHours(-1)));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeExclusive, "sync"),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("another-machine", refusal.Message, StringComparison.Ordinal);

        await using var forced = await runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeExclusive, "sync") with { Force = true },
            TestContext.Current.CancellationToken);

        Assert.Single(runLock.Read(layout));
        Assert.Contains("--force-lock", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VariantsShareATree_ButNeverTheSameVariant()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);
        var layout = Layout(temp);

        await using var msvc = await runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeShared, "build") with { Variant = "x86_64-msvc-release" },
            TestContext.Current.CancellationToken);

        // Another variant of the same tree builds beside it: each has its own build directory.
        await using var gcc = await runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeShared, "build") with { Variant = "x86_64-gcc-release" },
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HarnessException>(() => runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeShared, "test") with { Variant = "x86_64-msvc-release" },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, runLock.Read(layout).Count);
    }

    [Fact]
    public async Task ASyncTakesTheWholeTree_AndABuildKeepsASyncOut()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);
        var layout = Layout(temp);

        await using (await runLock.AcquireAsync(layout, Request(LockScope.TreeExclusive, "sync"), TestContext.Current.CancellationToken))
        {
            // Nothing may build while its sources are being replaced.
            await Assert.ThrowsAsync<HarnessException>(() => runLock.AcquireAsync(
                layout,
                Request(LockScope.TreeShared, "build") with { Variant = "x86_64-msvc-release" },
                TestContext.Current.CancellationToken));
        }

        await using var build = await runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeShared, "build") with { Variant = "x86_64-msvc-release" },
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HarnessException>(() => runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeExclusive, "sync"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ALockIsReleasedOnlyByTheRunThatTookIt()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);
        var layout = Layout(temp);

        // Another run, on another machine, holding another variant of the same tree.
        Write(layout, Entry("another-machine", 4321, DateTimeOffset.UtcNow, variant: "x86_64-gcc-release", scope: "TreeShared"));

        var handle = await runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeShared, "build") with { Variant = "x86_64-msvc-release" },
            TestContext.Current.CancellationToken);

        await handle.ReleaseAsync(TestContext.Current.CancellationToken);

        var remaining = runLock.Read(layout).Single();
        Assert.Equal("another-machine", remaining.Holder.Machine);
        Assert.Equal("x86_64-gcc-release", remaining.Variant);

        // Releasing twice is not an error, and takes nothing else with it.
        await handle.ReleaseAsync(TestContext.Current.CancellationToken);
        Assert.Single(runLock.Read(layout));
    }

    [Fact]
    public async Task ALockFileThatCannotBeRead_IsNeverReadAsFree()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);
        var layout = Layout(temp);

        Directory.CreateDirectory(Path.GetDirectoryName(layout.LockFile)!);
        await File.WriteAllTextAsync(layout.LockFile, "{ this is not the lock file }", TestContext.Current.CancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => runLock.AcquireAsync(
            layout,
            Request(LockScope.TreeExclusive, "sync"),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
    }

    [Fact]
    public async Task TwoRunsAskingAtOnce_LeaveExactlyOneHolder()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var layout = Layout(temp);

        // Separate instances, as two processes would be: the file is read, decided and written as
        // one step, or both would read "free" and both would write themselves in.
        var attempts = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            var runLock = new RunLock(factory.FileSystem, factory.Output);

            try
            {
                await runLock.AcquireAsync(layout, Request(LockScope.TreeExclusive, $"sync {index}"), TestContext.Current.CancellationToken);
                return true;
            }
            catch (HarnessException)
            {
                return false;
            }
        }));

        var taken = await Task.WhenAll(attempts);

        Assert.Equal(1, taken.Count(success => success));
        Assert.Single(new RunLock(factory.FileSystem, factory.Output).Read(layout));
    }

    [Fact]
    public async Task ASharedLockWithNoVariant_IsRefused()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runLock = new RunLock(factory.FileSystem, factory.Output);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => runLock.AcquireAsync(
            Layout(temp),
            Request(LockScope.TreeShared, "build"),
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
    }

    private static HarnessLayout Layout(TempDirectory temp) => new(temp.Path, temp.Path);

    private static LockRequest Request(LockScope scope, string command) => new()
    {
        Host = "local",
        Tree = "/repo",
        Scope = scope,
        RunId = RunId.New(),
        Command = command,
    };

    /// <summary>A lock file holding one entry, written as another run would have left it.</summary>
    private static void Write(
        HarnessLayout layout,
        string entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(layout.LockFile)!);
        File.WriteAllText(layout.LockFile, "[" + entry + "]");
    }

    private static string Entry(
        string machine,
        int processId,
        DateTimeOffset startedUtc,
        string? variant = null,
        string scope = "TreeExclusive")
    {
        var stamp = startedUtc.ToString("O", CultureInfo.InvariantCulture);
        var id = processId.ToString(CultureInfo.InvariantCulture);
        var variantField = variant is null ? string.Empty : $"\"variant\": \"{variant}\",";

        return "{ \"host\": \"local\", \"tree\": \"/repo\", " + variantField + " \"scope\": \"" + scope + "\", "
            + "\"holder\": { \"machine\": \"" + machine + "\", \"processId\": " + id + ", "
            + "\"processStartedUtc\": \"" + stamp + "\", \"runId\": \"20250101-120000-deadbeef\", "
            + "\"takenUtc\": \"" + stamp + "\", \"command\": \"test\" } }";
    }
}
