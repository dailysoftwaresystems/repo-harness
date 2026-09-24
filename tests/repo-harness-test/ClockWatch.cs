using System.Diagnostics;

namespace RepoHarness.Tests;

/// <summary>
/// Watches this machine's wall clock against its monotonic clock while a test that needs an honest one
/// runs, and says whether it held.
/// </summary>
/// <remarks>
/// A virtual machine's clock is not always honest. Measured in WSL while a real build ran, the wall clock
/// stepped back by 24.8 seconds, and forward again by as much, every five seconds; on another day its VM
/// paused, and a phase that took a fraction of a second read as 26. A build system ordering file times
/// cannot build on such a clock - ninja failed builds outright on it - and nothing that orders two times
/// can be judged on it, so what a test of either sees there proves nothing about the code.
/// <para>
/// Read every 50 milliseconds on a thread of its own, so a busy thread pool cannot hold a reading back,
/// and on Windows above the priority of the work it watches: measured there with every core spinning,
/// readings at normal priority came half a second apart. On Linux the priority changes nothing, and a
/// blocking garbage collection holds readings back on every platform. What it adds up is each jump of 5
/// milliseconds or more between two readings, forward or back: that sees a step and its return that net
/// out between two readings taken at the ends, and steps of that size that add up. A clock the system
/// slews moves by less than that between two readings taken on time - ntpd, timesyncd and Windows Time by
/// a fraction of a millisecond, chrony at its fastest by about 4 - and adds nothing.
/// </para>
/// </remarks>
internal sealed class ClockWatch : IDisposable
{
    /// <summary>How often the two clocks are read against each other.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(50);

    /// <summary>The least jump between two readings that is a step: more than any slewing clock moves in one.</summary>
    private static readonly TimeSpan Jump = TimeSpan.FromMilliseconds(5);

    /// <summary>How far the steps may add up to before the clock did not hold.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _reader;
    private DateTimeOffset _wall;
    private long _monotonic;
    private TimeSpan _stepped;
    private int _disposed;

    /// <summary>Starts watching.</summary>
    public ClockWatch()
    {
        _wall = DateTimeOffset.UtcNow;
        _monotonic = Stopwatch.GetTimestamp();
        _reader = new Thread(() =>
        {
            while (!_stop.Wait(Every))
            {
                Read();
            }
        })
        {
            IsBackground = true,
            Name = "clock watch",
            Priority = ThreadPriority.AboveNormal,
        };
        _reader.Start();
    }

    /// <summary>How far the steps seen so far add up to, each forward or back.</summary>
    public TimeSpan Stepped
    {
        get
        {
            Read();

            lock (_gate)
            {
                return _stepped;
            }
        }
    }

    /// <summary>Whether its steps added up to less than a second throughout.</summary>
    public bool Held => Stepped < Tolerance;

    /// <summary>What the clock did, for a skip's reason.</summary>
    public string Seen => $"this machine's clock stepped by {Stepped.TotalSeconds:0.0} seconds in all while it ran";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _stop.Set();
        _reader.Join();
        _stop.Dispose();
    }

    private void Read()
    {
        lock (_gate)
        {
            var wall = DateTimeOffset.UtcNow;
            var monotonic = Stopwatch.GetTimestamp();
            var jump = (wall - _wall - Stopwatch.GetElapsedTime(_monotonic, monotonic)).Duration();

            if (jump >= Jump)
            {
                _stepped += jump;
            }

            _wall = wall;
            _monotonic = monotonic;
        }
    }
}
