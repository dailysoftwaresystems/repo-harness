using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The machine-wide lock every read-decide-write here is taken under, opened through the race the
/// runtime's own first opening has on Linux and macOS.
/// </summary>
public sealed class MachineWideMutexTests
{
    /// <summary>
    /// An open that fails for a moment - as two processes racing to create the runtime's directory for
    /// named mutexes make it fail - is tried again, a little later each time, and the mutex it then
    /// opens is the one returned.
    /// </summary>
    [Fact]
    public void AnOpenThatFailsForAMoment_IsTriedAgain_ALittleLaterEachTime()
    {
        var name = MachineWideMutex.NameFor("test", [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))], ignoreCase: true);
        var tries = 0;
        var pauses = new List<TimeSpan>();

        using var mutex = MachineWideMutex.Open(
            name,
            "a test's subject",
            () => ++tries < 3
                ? throw new IOException("One or more system calls failed: stat(\"/tmp/.dotnet/shm\", ...) == -1; errno == ENOENT;")
                : new Mutex(initiallyOwned: false, name),
            pauses.Add);

        Assert.Equal(3, tries);
        Assert.Equal([MachineWideMutex.Backoff, MachineWideMutex.Backoff * 2], pauses);
        Assert.True(MachineWideMutex.Wait(mutex, TimeSpan.FromSeconds(5)));
        mutex.ReleaseMutex();
    }

    /// <summary>
    /// One that keeps failing is said as this machine being unavailable, naming the lock, what the system
    /// said the last time and the fix - never an internal error, never a refusal that would end a run of
    /// which this machine is one host, and never a run that goes on without the lock.
    /// </summary>
    [Fact]
    public void AnOpenThatKeepsFailing_IsUnavailable_NamingTheLockAndWhatTheSystemSaidLast()
    {
        var tries = 0;

        var refusal = Assert.Throws<HarnessException>(() => MachineWideMutex.Open(
            @"Global\repo-harness-test-lock",
            "a test's subject",
            () =>
            {
                tries++;
                throw new IOException($"One or more system calls failed: mkdir(\"/tmp/.dotnet/shm/global\", ...) == -1; errno == EEXIST; try {tries}");
            },
            _ => { }));

        Assert.Equal(MachineWideMutex.Attempts, tries);
        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Contains(@"'Global\repo-harness-test-lock' on a test's subject", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"errno == EEXIST; try {MachineWideMutex.Attempts}.", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("fix what the system said, and run the command again", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A mutex another user holds is refused at once: no retry makes it this user's.</summary>
    [Fact]
    public void AMutexAnotherUserHolds_IsRefusedAtOnce()
    {
        var tries = 0;

        var refusal = Assert.Throws<HarnessException>(() => MachineWideMutex.Open(
            @"Global\repo-harness-test-lock",
            "a test's subject",
            () =>
            {
                tries++;
                throw new UnauthorizedAccessException();
            },
            _ => throw new InvalidOperationException("paused before refusing")));

        Assert.Equal(1, tries);
        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("belongs to another user", refusal.Message, StringComparison.Ordinal);
    }
}
