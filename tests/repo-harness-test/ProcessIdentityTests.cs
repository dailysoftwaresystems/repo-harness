using System.Globalization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// What tells one process from the next holder of its id. This is what a lock and a log claim record,
/// and the one rule it must keep is that no wall clock is in it: a host this tool serves steps its
/// clock forward by about 25 seconds every few seconds, and a stamp that moved with it would make
/// every live holder on that host read as dead at once.
/// </summary>
public sealed class ProcessIdentityTests
{
    [Fact]
    public void ThisProcess_IsAliveUnderItsOwnStamp()
    {
        var identity = Identity();

        Assert.True(identity.IsAlive(identity.CurrentId, identity.Current));
    }

    [Fact]
    public void ThisProcessesId_UnderAnotherStamp_IsNotThisProcess()
    {
        // A recycled id: something live carries it, and it is not what recorded the stamp.
        var identity = Identity();

        Assert.False(identity.IsAlive(identity.CurrentId, "a-stamp-no-process-here-carries"));
    }

    [Fact]
    public void AnIdNothingCarries_IsNotAlive()
    {
        Assert.False(Identity().IsAlive(int.MaxValue - 1, "anything at all"));
    }

    /// <summary>
    /// An id recorded by something that could not be told apart from a later holder of it is read as
    /// alive while anything carries it: taking what a live process holds is the worse mistake.
    /// </summary>
    [Fact]
    public void AnIdRecordedWithNoStamp_IsReadAsAlive_WhileSomethingCarriesIt()
    {
        var identity = Identity();

        Assert.True(identity.IsAlive(identity.CurrentId, null));
        Assert.False(identity.IsAlive(int.MaxValue - 1, null));
    }

    /// <summary>
    /// The regression this class exists for. Linux publishes a start time in ticks since boot, and
    /// the wall clock cannot move those. Adding a boot time to them — which is what both the process
    /// table and .NET's own <c>Process.StartTime</c> once did here, and what <c>ps lstart</c> still
    /// does — puts the clock back in and hands every live process a new identity when it steps.
    /// </summary>
    [Fact]
    public void OnLinux_TheStampIsBootAndTicks_WithNoClockInIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux publishes ticks since boot; see the start-time test.");

        var stat = File.ReadAllText($"/proc/{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}/stat");
        var ticks = ProcStat.Number(ProcStat.FieldsAfterName(stat), ProcStat.StartTicksField);
        var boot = File.ReadAllText(ProcStat.BootIdPath).Trim();

        Assert.NotNull(ticks);
        Assert.Equal($"{boot}:{ticks!.Value.ToString(CultureInfo.InvariantCulture)}", Identity().Current, StringComparer.Ordinal);
    }

    /// <summary>
    /// Windows and macOS record the start time once, when the process is created, and never work it
    /// out again — so it is already clock-proof. It is read as its own ticks rather than converted to
    /// an instant, because a conversion is the one way a stored value could still come back
    /// differently twice: the offset a local time converts through is not a fact about the process.
    /// </summary>
    [Fact]
    public void OffLinux_TheStampIsTheRecordedStartTicks_WithNoConversion()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Linux works its start time out from boot; see the boot-and-ticks test.");

        using var current = System.Diagnostics.Process.GetCurrentProcess();

        Assert.Equal(
            current.StartTime.Ticks.ToString(CultureInfo.InvariantCulture),
            Identity().Current,
            StringComparer.Ordinal);
    }

    [Fact]
    public void TheStamp_IsTheSameEveryTimeItIsRead()
    {
        var identity = Identity();
        var first = identity.Current;

        Assert.Equal(first, Identity().Current, StringComparer.Ordinal);
        Assert.Equal(first, identity.Current, StringComparer.Ordinal);
    }

    private static IProcessIdentity Identity() => new ProcessIdentity(new HostPlatform());
}
