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

    /// <summary>
    /// The cap is per machine, so two machines run their own legs at the same time. Measured by
    /// holding every leg until one from each machine is inside: that only ever happens if the two
    /// machines really do proceed independently, so a cap applied across the run would hang here
    /// rather than merely report a different number.
    /// </summary>
    [Fact]
    public async Task MaxParallelLegs_CapsEachMachineSeparately_SoOtherMachinesKeepRunning()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        var gate = new Gate(participants: 2);
        var peaks = new MachinePeaks();

        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs =
                [
                    Leg("win", "machine:local"),
                    Leg("wsl", "machine:local"),
                    Leg("vps-one", "ssh:vps-a"),
                    Leg("vps-two", "ssh:vps-a"),
                ],
                MaxParallelLegs = 1,
                RunLeg = async (leg, token) =>
                {
                    using (peaks.Enter(leg.MachineKey))
                    {
                        await gate.ArriveAsync(token);
                    }

                    return Passed(leg);
                },
            },
            ledger,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, execution.Entries.Count);

        // One at a time on each machine, and both machines at once: the gate needed two.
        Assert.Equal(1, peaks.Peak("machine:local"));
        Assert.Equal(1, peaks.Peak("ssh:vps-a"));
        Assert.Equal(2, gate.Peak);
    }

    /// <summary>
    /// The overall ceiling applies on top of the per-machine cap, so a wide fleet cannot start
    /// everything at once just because each machine is under its own limit.
    /// </summary>
    [Fact]
    public async Task MaxParallelLegsTotal_CapsTheWholeRun_AcrossMachines()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        var gate = new Gate(participants: 2);

        // Four machines, each well under its own cap of two, but a ceiling of two overall. The gate
        // releases at two, so the run finishes only if two ran together, and the peak says whether a
        // third joined them.
        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs =
                [
                    Leg("a", "ssh:a"),
                    Leg("b", "ssh:b"),
                    Leg("c", "ssh:c"),
                    Leg("d", "ssh:d"),
                ],
                MaxParallelLegs = 2,
                MaxParallelLegsTotal = 2,
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

    /// <summary>
    /// A leg that says nothing about where it runs is counted as sharing one machine with every
    /// other such leg, so the cap still binds. The opposite guess would remove it silently.
    /// </summary>
    [Fact]
    public async Task LegsWithNoMachine_ShareOneMachine_SoTheCapStillBinds()
    {
        var factory = new HarnessFactory();
        var ledger = new LegLedger(factory.Output, "test");
        var gate = new Gate(participants: 2);
        var peaks = new MachinePeaks();

        var execution = await Executor(factory).RunAsync(
            new LegExecutionRequest
            {
                Legs = [Leg("one"), Leg("two"), Leg("three"), Leg("four")],
                MaxParallelLegs = 2,
                RunLeg = async (leg, token) =>
                {
                    using (peaks.Enter("all"))
                    {
                        await gate.ArriveAsync(token);
                    }

                    return Passed(leg);
                },
            },
            ledger,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, execution.Entries.Count);
        Assert.Equal(2, peaks.Peak("all"));
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

    private static LegPlan Leg(string name, string machine) => Leg(name) with { MachineKey = machine };

    private static LegEntry Passed(LegPlan leg) => new()
    {
        Leg = leg.Name,
        Verdict = LegVerdict.Passed,
        Duration = TimeSpan.FromSeconds(1),
        Emulated = leg.Emulated,
    };

    /// <summary>
    /// Records, per machine, how many of its legs were ever running at one moment.
    /// </summary>
    /// <remarks>
    /// Counted around the whole of a leg's work rather than at one instant, so a cap that is not
    /// applied is caught however the threads happen to interleave: the count only falls when a leg
    /// actually finishes.
    /// </remarks>
    private sealed class MachinePeaks
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _inside = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _peak = new(StringComparer.Ordinal);

        public int Peak(string machine)
        {
            lock (_gate)
            {
                return _peak.TryGetValue(machine, out var peak) ? peak : 0;
            }
        }

        public IDisposable Enter(string machine)
        {
            lock (_gate)
            {
                var inside = _inside.TryGetValue(machine, out var current) ? current + 1 : 1;
                _inside[machine] = inside;

                if (inside > (_peak.TryGetValue(machine, out var peak) ? peak : 0))
                {
                    _peak[machine] = inside;
                }
            }

            return new Exit(this, machine);
        }

        private sealed class Exit(MachinePeaks peaks, string machine) : IDisposable
        {
            public void Dispose()
            {
                lock (peaks._gate)
                {
                    peaks._inside[machine]--;
                }
            }
        }
    }

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
