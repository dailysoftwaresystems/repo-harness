using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>Serialises changes to one pair of registries across every DssHarness process on the machine.</summary>
public interface IAnchorRegistryLock
{
    /// <summary>
    /// Runs <paramref name="work"/> while holding the lock for <paramref name="registries"/>.
    /// </summary>
    /// <remarks>
    /// The work is synchronous on purpose, and must stay so: the lock is released by the thread that
    /// took it, and an await inside the work could resume on a different one.
    /// </remarks>
    /// <exception cref="HarnessException">Another process held the lock for longer than the wait allows.</exception>
    T RunExclusive<T>(AnchorRegistries registries, Func<T> work);
}

/// <inheritdoc cref="IAnchorRegistryLock"/>
/// <remarks>
/// A change reads both registries, decides, and writes them back, so two changes at once would each
/// write over the other and one would be lost without a word. A named mutex is the machine-wide lock
/// .NET supports on Windows, Linux and macOS alike; a named semaphore is not available on Linux or macOS.
/// Anchor changes take milliseconds, so the wait is short, and a lock still held after it is reported
/// rather than waited on indefinitely.
/// </remarks>
public sealed class NamedMutexAnchorRegistryLock(IHostPlatform platform, TimeSpan timeout) : IAnchorRegistryLock
{
    /// <summary>How long a change waits for another before refusing.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IHostPlatform _platform = platform;
    private readonly TimeSpan _timeout = timeout;

    public T RunExclusive<T>(AnchorRegistries registries, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(work);

        using var mutex = MachineWideMutex.Open(NameFor(registries), "the anchor registries");

        if (!MachineWideMutex.Wait(mutex, _timeout))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"Another {ToolPackage.Command} process has held the anchor registries for {_timeout.TotalSeconds:0} seconds, "
                + "so nothing was changed. Run the command again once it has finished.");
        }

        try
        {
            return work();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>The lock's name: one per pair of registry files, however their paths are spelled.</summary>
    public string NameFor(AnchorRegistries registries)
    {
        ArgumentNullException.ThrowIfNull(registries);

        return MachineWideMutex.NameFor(
            "anchors",
            registries.All.Select(registry => registry.FullPath),
            _platform.PathComparison == StringComparison.OrdinalIgnoreCase);
    }
}
