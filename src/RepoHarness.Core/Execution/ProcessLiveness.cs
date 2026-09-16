using System.ComponentModel;
using System.Diagnostics;

namespace RepoHarness.Core.Execution;

/// <summary>
/// Whether the process that took something still holds it.
/// </summary>
/// <remarks>
/// Staleness is decided by liveness, never by a timeout: a timeout is a guess about how long
/// honest work takes, and it eventually breaks an honest run. A pid alone cannot answer the
/// question either, because pids are recycled — on Windows a freed one was measured coming back
/// after about a hundred allocations — so the start time is checked with it.
/// </remarks>
internal static class ProcessLiveness
{
    /// <summary>
    /// How far two readings of one process's start time may differ and still be the same process.
    /// Not a loophole: a recycled id would have to be handed out and its process started inside the
    /// same second, while a platform that reports the start time coarsely is ordinary.
    /// </summary>
    private static readonly TimeSpan SameStart = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The start time recorded by a process that could not read its own. It marks the fact as
    /// missing rather than guessing at it, because a guessed start time would not match the
    /// process's real one and every later run would read it as dead.
    /// </summary>
    public static readonly DateTimeOffset UnknownStart = default;

    /// <summary>This process's id, recorded in whatever it takes.</summary>
    public static int CurrentId => Environment.ProcessId;

    /// <summary>
    /// When this process started, recorded beside its id so that a later run can tell this process
    /// from another one that inherited the id.
    /// </summary>
    public static DateTimeOffset CurrentStartedUtc { get; } = ReadCurrentStart();

    /// <summary>The machine this process runs on, recorded so a holder elsewhere is recognisable as elsewhere.</summary>
    public static string CurrentMachine => Environment.MachineName;

    /// <summary>
    /// Whether the process that recorded <paramref name="processId"/> and
    /// <paramref name="startedUtc"/> is still running.
    /// </summary>
    /// <param name="processId">The recorded id.</param>
    /// <param name="startedUtc">The recorded start time.</param>
    public static bool IsAlive(int processId, DateTimeOffset startedUtc)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);

            if (process.HasExited)
            {
                return false;
            }

            if (startedUtc == UnknownStart)
            {
                // Whoever recorded this could not read its own start time, so the id is all there
                // is to go on. Reported as alive: taking what a live process holds is the worse
                // mistake of the two, and this one is rare enough to be worth its cost.
                return true;
            }

            var started = new DateTimeOffset(process.StartTime).ToUniversalTime();
            return Math.Abs((started - startedUtc).TotalSeconds) <= SameStart.TotalSeconds;
        }
        catch (ArgumentException)
        {
            // No process carries that id, so whoever recorded it has gone.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // The process exists and this user may not ask it anything. Reported as alive: an
            // unreadable answer is never read as "it has gone", which would take what it holds.
            return true;
        }
    }

    private static DateTimeOffset ReadCurrentStart()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return new DateTimeOffset(current.StartTime).ToUniversalTime();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Recorded as missing rather than as a guess: a later run then goes by the id alone and
            // leaves what this one holds alone, instead of reclaiming it from a process still using it.
            return UnknownStart;
        }
    }
}
