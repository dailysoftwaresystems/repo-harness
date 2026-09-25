using RepoHarness.Core.Hosts;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// A host that sleeps between commands is given its window to wake: its name is looked up again, and a
/// connection nothing took is tried again, until either answers or the window ends - and nothing a host that
/// is awake says is ever tried again.
/// </summary>
public sealed class SshWakeWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 18, 0, 0, TimeSpan.Zero);

    /// <summary>A name that answers on the window's third lookup resolves, counted with the lookups before it, to its IPv4 address.</summary>
    [Fact]
    public async Task AName_ThatAnswersPartWayThroughTheWindow_Resolves_CountingEveryLookup()
    {
        var clock = new ManualClock(Start);
        var lookup = new ScriptedLookup(clock, answersOnCall: 3, step: TimeSpan.FromSeconds(1));
        var window = new SshWakeWindow(lookup, clock, TimeSpan.Zero);

        var resolution = await window.KeepResolvingAsync(Missed("mac.local"), window.Now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(resolution.Resolved);
        Assert.Equal(6, resolution.Attempts);
        Assert.Equal("192.0.2.7", resolution.ResolvedTo);
    }

    /// <summary>A name that never answers is looked up until the window ends, and no longer.</summary>
    [Fact]
    public async Task AName_ThatNeverAnswers_IsLookedUpUntilTheWindowEnds()
    {
        var clock = new ManualClock(Start);
        var lookup = new ScriptedLookup(clock, answersOnCall: int.MaxValue, step: TimeSpan.FromSeconds(10));
        var window = new SshWakeWindow(lookup, clock, TimeSpan.Zero);

        var resolution = await window.KeepResolvingAsync(Missed("mac.local"), window.Now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(resolution.Resolved);
        Assert.Equal(3, lookup.Calls);
        Assert.Equal(6, resolution.Attempts);
    }

    /// <summary>
    /// A connection nothing took, or that timed out, is tried again until one is taken; each try that itself
    /// took time is charged to the window.
    /// </summary>
    [Fact]
    public async Task AConnection_NothingTookYet_IsTriedAgainUntilOneIsTaken()
    {
        var clock = new ManualClock(Start);
        var tries = new Queue<ProcessResult>([Refused(), TimedOut(), Answered()]);
        var window = new SshWakeWindow(new ScriptedLookup(clock, int.MaxValue, TimeSpan.Zero), clock, TimeSpan.Zero);

        var (result, attempts) = await window.KeepProbingAsync(
            Refused(),
            window.Now,
            TimeSpan.FromSeconds(60),
            _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                return Task.FromResult(tries.Dequeue());
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(4, attempts);
    }

    /// <summary>
    /// A window lasts as long as it says however the time of day is set meanwhile: measured by the time of day, a
    /// clock set ahead ended it after one try - a flaky test on a machine whose clock steps showed it - and one set
    /// back would draw it out by as much.
    /// </summary>
    [Fact]
    public async Task AWindow_LastsItsLength_HoweverTheTimeOfDayIsSetMeanwhile()
    {
        var clock = new SetAheadClock(Start);
        var tries = new Queue<ProcessResult>([Refused(), TimedOut(), Answered()]);
        var window = new SshWakeWindow(new ScriptedLookup(clock, int.MaxValue, TimeSpan.Zero), clock, TimeSpan.Zero);

        var (result, attempts) = await window.KeepProbingAsync(
            Refused(),
            window.Now,
            TimeSpan.FromSeconds(60),
            _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                return Task.FromResult(tries.Dequeue());
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(4, attempts);
    }

    /// <summary>
    /// A host that answered - a key it showed that the name is not known by, a login refused - would answer the
    /// same however long it was waited for, so it is never waited on.
    /// </summary>
    [Theory]
    [InlineData("Host key verification failed.")]
    [InlineData("harness@host: Permission denied (publickey).")]
    public async Task AHostThatAnswered_IsNeverTriedAgain(string said)
    {
        var clock = new ManualClock(Start);
        var window = new SshWakeWindow(new ScriptedLookup(clock, int.MaxValue, TimeSpan.Zero), clock, TimeSpan.Zero);
        var failed = HostResults.Failed(HostProbes.SshFailed, said + "\n");

        var (result, attempts) = await window.KeepProbingAsync(
            failed,
            window.Now,
            TimeSpan.FromSeconds(60),
            _ => throw new InvalidOperationException("A host that answered was tried again."),
            TestContext.Current.CancellationToken);

        Assert.Same(failed, result);
        Assert.Equal(1, attempts);
    }

    /// <summary>
    /// Why a host's window ran out is kept for the half minute a name's answer is, so the other legs a command
    /// places there are refused at once rather than each waiting the window again; and then forgotten.
    /// </summary>
    [Fact]
    public void WhyAWindowRanOut_IsKeptForAMoment_AndThenForgotten()
    {
        var clock = new ManualClock(Start);
        var window = new SshWakeWindow(new ScriptedLookup(clock, int.MaxValue, TimeSpan.Zero), clock, TimeSpan.Zero);
        var mac = HostId.Ssh("mac");

        Assert.Null(window.RanOut(mac));

        window.Remember(mac, "it did not wake");
        clock.Advance(HostAddressResolver.CacheLifetime - TimeSpan.FromSeconds(1));

        Assert.Equal("it did not wake", window.RanOut(mac));
        Assert.Null(window.RanOut(HostId.Ssh("vps")));

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Null(window.RanOut(mac));
    }

    private static AddressResolution Missed(string name) => new(name, Resolved: false, Attempts: HostAddressResolver.Attempts);

    private static ProcessResult Refused() => HostResults.Failed(HostProbes.SshFailed, "ssh: connect to host mac.local port 22: Connection refused\n");

    private static ProcessResult TimedOut() => new(-1, string.Empty, string.Empty, TimeSpan.FromSeconds(30), TimedOut: true);

    private static ProcessResult Answered() => HostResults.Ok("%COMSPEC%\n");

    /// <summary>A clock that moves only when told, its timestamps with it.</summary>
    private class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly DateTimeOffset _start = start;
        private DateTimeOffset _now = start;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => (_now - _start).Ticks;

        public virtual void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// A clock whose time of day is set an hour ahead each time it moves, as a machine syncing its time sets it,
    /// while time itself passes as told.
    /// </summary>
    private sealed class SetAheadClock(DateTimeOffset start) : ManualClock(start)
    {
        private TimeSpan _setAhead;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _setAhead;

        public override void Advance(TimeSpan by)
        {
            base.Advance(by);
            _setAhead += TimeSpan.FromHours(1);
        }
    }

    /// <summary>A name that answers from a given call on, each lookup taking the time it is told to.</summary>
    private sealed class ScriptedLookup(ManualClock clock, int answersOnCall, TimeSpan step) : INameLookup
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
        {
            Calls++;
            clock.Advance(step);

            return Task.FromResult<IReadOnlyList<string>>(Calls >= answersOnCall ? ["fe80::7", "192.0.2.7"] : []);
        }
    }
}
