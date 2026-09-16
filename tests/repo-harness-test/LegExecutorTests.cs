using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// Legs are isolated from one another, so a command runs every selected leg at once and waits for
/// all of them. What that must never do is lose a leg: not to a cap on parallelism, not to a shared
/// build directory, not to an exception, and not to an interrupted run.
/// </summary>
public sealed class LegExecutorTests
{
    [Fact]
    public async Task EverySelectedLeg_Runs_AndIsReportedInSelectionOrder()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        var ran = new List<string>();

        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [Leg("win-msvc-release"), Leg("wsl-clang-asan"), Leg("mac-clang-release")],
                RunLeg = (leg, _) =>
                {
                    lock (ran)
                    {
                        ran.Add(leg.Name);
                    }

                    return Task.FromResult<LegEntry?>(Passed(leg));
                },
            },
            ledger,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, ran.Count);
        Assert.Equal(["win-msvc-release", "wsl-clang-asan", "mac-clang-release"], execution.Entries.Select(entry => entry.Leg));
        Assert.Empty(execution.Unfinished);
        Assert.False(execution.Cancelled);
        Assert.Equal(3, ledger.Entries.Count);
    }

    [Fact]
    public async Task MaxParallelLegs_CapsHowManyRunAtOnce()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        var gate = new Gate(participants: 2);

        // Four legs and a cap of two. The gate releases as soon as two are inside, so the run only
        // finishes if two really do run together, and the peak says whether a third joined them.
        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [Leg("one"), Leg("two"), Leg("three"), Leg("four")],
                MaxParallelLegs = 2,
                RunLeg = async (leg, token) =>
                {
                    await gate.ArriveAsync(token);
                    return Passed(leg);
                },
            },
            ledger,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, execution.Entries.Count);
        Assert.Equal(2, gate.Peak);
    }

    [Fact]
    public async Task WithNoCap_EveryLegStartsAtOnce()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        var gate = new Gate(participants: 4);

        // Nothing is released until all four are inside, so this run finishes only if all four ran
        // together. A cap left unset must start every selected leg immediately.
        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [Leg("one"), Leg("two"), Leg("three"), Leg("four")],
                MaxParallelLegs = null,
                RunLeg = async (leg, token) =>
                {
                    await gate.ArriveAsync(token);
                    return Passed(leg);
                },
            },
            ledger,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, execution.Entries.Count);
        Assert.Equal(4, gate.Peak);
    }

    [Fact]
    public async Task TwoLegsResolvingToOneBuildDirectory_AreRefusedBeforeEitherStarts()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        var started = 0;

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs =
                [
                    Leg("win-msvc-release") with { BuildDirectory = "/repo/build/x86_64-msvc-release" },
                    Leg("win-msvc-release-again") with { BuildDirectory = "/repo/build/x86_64-msvc-release" },
                ],
                RunLeg = (leg, _) =>
                {
                    Interlocked.Increment(ref started);
                    return Task.FromResult<LegEntry?>(Passed(leg));
                },
            },
            ledger,
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("win-msvc-release", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, started);
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public async Task TheSameBuildDirectoryOnTwoHosts_IsNotACollision()
    {
        // Two hosts commonly keep their copy at the same path, and one variant on two machines is
        // exactly the "run this suite on both" case the tool exists for. Comparing paths alone
        // would refuse it as a collision that does not exist.
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");

        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs =
                [
                    Leg("vps-release") with { BuildDirectory = "/home/dev/repo/build/x86_64-gcc-release", TreeKey = "ssh vps /home/dev/repo" },
                    Leg("mac-release") with { BuildDirectory = "/home/dev/repo/build/x86_64-gcc-release", TreeKey = "ssh mac /home/dev/repo" },
                ],
                RunLeg = (leg, _) => Task.FromResult<LegEntry?>(Passed(leg)),
            },
            ledger,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, execution.Entries.Count);
    }

    [Fact]
    public async Task ATreeIsSyncedOnce_HoweverManyLegsShareIt()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        var synced = new List<string>();

        await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs =
                [
                    Leg("msvc") with { TreeKey = "local:/repo" },
                    Leg("gcc") with { TreeKey = "local:/repo" },
                    Leg("wsl") with { TreeKey = "wsl Ubuntu:/home/repo" },
                ],
                SyncTree = (tree, _) =>
                {
                    lock (synced)
                    {
                        synced.Add(tree);
                    }

                    return Task.CompletedTask;
                },
                RunLeg = (leg, _) => Task.FromResult<LegEntry?>(Passed(leg)),
            },
            ledger,
            TestContext.Current.CancellationToken);

        // If each leg synced, their copies would race over the same files.
        Assert.Equal(2, synced.Count);
        Assert.Equal(["local:/repo", "wsl Ubuntu:/home/repo"], synced.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ALegThatReachesNoVerdict_IsPoisonedRatherThanDropped()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");

        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [Leg("silent"), Leg("honest")],
                RunLeg = (leg, _) => Task.FromResult<LegEntry?>(leg.Name == "silent" ? null : Passed(leg)),
            },
            ledger,
            TestContext.Current.CancellationToken);

        var silent = execution.Entries.Single(entry => entry.Leg == "silent");

        Assert.Equal(LegVerdict.Poisoned, silent.Verdict);
        Assert.Contains("silent", silent.Detail, StringComparison.Ordinal);
        Assert.Equal(LegVerdict.Passed, execution.Entries.Single(entry => entry.Leg == "honest").Verdict);
    }

    [Fact]
    public async Task ARefusedLeg_KeepsWhatTheRefusalMeant()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");

        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [Leg("vps-arm64-gcc-rel"), Leg("local")],
                RunLeg = (leg, _) => leg.Name == "local"
                    ? Task.FromResult<LegEntry?>(Passed(leg))
                    : throw new HarnessException(HarnessExit.HostUnavailable, "ssh vps: ssh could not connect"),
            },
            ledger,
            TestContext.Current.CancellationToken);

        var unavailable = execution.Entries.Single(entry => entry.Leg == "vps-arm64-gcc-rel");

        // A switched-off machine is not a defect in the harness, and reporting it as poisoned would
        // send the reader looking for one.
        Assert.Equal(LegVerdict.SkippedUnavailable, unavailable.Verdict);
        Assert.Contains("ssh could not connect", unavailable.Detail, StringComparison.Ordinal);
        Assert.False(Verdicts.IsFailure(unavailable.Verdict));
    }

    [Fact]
    public async Task ALegThatThrows_IsPoisoned_AndTheOthersStillReport()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");

        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [Leg("broken"), Leg("fine")],
                RunLeg = (leg, _) => leg.Name == "broken"
                    ? throw new InvalidOperationException("the build directory vanished")
                    : Task.FromResult<LegEntry?>(Passed(leg)),
            },
            ledger,
            TestContext.Current.CancellationToken);

        var broken = execution.Entries.Single(entry => entry.Leg == "broken");

        Assert.Equal(LegVerdict.Poisoned, broken.Verdict);
        Assert.Contains("the build directory vanished", broken.Detail, StringComparison.Ordinal);
        Assert.Equal(LegVerdict.Passed, execution.Entries.Single(entry => entry.Leg == "fine").Verdict);
    }

    [Fact]
    public async Task AnInterruptedRun_ReportsWhatWasLeft()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var quickDone = new TaskCompletionSource();

        var run = Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [Leg("quick"), Leg("endless")],
                RunLeg = async (leg, token) =>
                {
                    if (leg.Name == "quick")
                    {
                        quickDone.TrySetResult();
                        return Passed(leg);
                    }

                    await Task.Delay(Timeout.Infinite, token);
                    return Passed(leg);
                },
            },
            ledger,
            cancellation.Token);

        await quickDone.Task;
        await cancellation.CancelAsync();

        var execution = await run;

        Assert.True(execution.Cancelled);
        Assert.Equal(["endless"], execution.Unfinished);
        Assert.Equal(["quick"], execution.Entries.Select(entry => entry.Leg));
        Assert.Contains("reached no verdict", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingNothing_IsRefused()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [],
                RunLeg = (leg, _) => Task.FromResult<LegEntry?>(Passed(leg)),
            },
            ledger,
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
    }

    private static LegExecutor Executor(HarnessFactory factory) => new(factory.Platform, factory.Output);

    private static LegPlan Leg(string name) => new()
    {
        Name = name,
        BuildDirectory = Path.Combine(Path.GetTempPath(), "build", name),
    };

    private static LegEntry Passed(LegPlan leg) => new()
    {
        Leg = leg.Name,
        Verdict = LegVerdict.Passed,
        Duration = TimeSpan.FromSeconds(1),
        Emulated = leg.Emulated,
    };

    /// <summary>
    /// Holds each leg until <paramref name="participants"/> of them are inside at once, and records
    /// how many were ever inside together. A cap that is not applied shows up as a larger peak; a
    /// cap that is too tight shows up as a run that never finishes.
    /// </summary>
    private sealed class Gate(int participants)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inside;
        private int _peak;

        /// <summary>The most legs that were inside at one moment.</summary>
        public int Peak => Volatile.Read(ref _peak);

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            var inside = Interlocked.Increment(ref _inside);

            for (var peak = Volatile.Read(ref _peak); inside > peak; peak = Volatile.Read(ref _peak))
            {
                Interlocked.CompareExchange(ref _peak, inside, peak);
            }

            if (inside >= participants)
            {
                _released.TrySetResult();
            }

            try
            {
                await _released.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }
}
