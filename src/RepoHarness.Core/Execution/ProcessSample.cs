using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

/// <summary>One reading of the machine's process table.</summary>
/// <param name="Index">Which reading this was; zero is the one taken as the leg started.</param>
/// <param name="Elapsed">Monotonic time since the leg started.</param>
/// <param name="Processes">What was running, machine-wide and not limited to this process's tree.</param>
/// <param name="Unreadable">
/// Why the table could not be read, when it could not. A sample that failed is reported as unknown
/// and never as nothing found: "no contender was running" and "nobody looked" are different facts.
/// </param>
public sealed record ProcessSample(int Index, TimeSpan Elapsed, IReadOnlyList<SampledProcess> Processes, string? Unreadable);

/// <summary>When a process was seen across a leg's samples.</summary>
public enum ProcessSeen
{
    /// <summary>In the first sample and the last, so it covered the whole leg.</summary>
    Throughout,

    /// <summary>In the first sample and gone by the last.</summary>
    AtTheStart,

    /// <summary>Absent from the first sample and running at the last.</summary>
    AtTheEnd,

    /// <summary>
    /// In neither the first sample nor the last, so it began and ended inside the leg. Reported
    /// because every sample is kept: with only a first and a last reading this contender is invisible.
    /// </summary>
    DuringTheRun,
}

/// <summary>A process outside the harness that was found using something this leg was using.</summary>
/// <param name="Process">The process, as the sample that first saw it reported it.</param>
/// <param name="Seen">When it was seen.</param>
/// <param name="Tool">The configured tool name it matched, so the report says which rule it broke.</param>
public sealed record ContendingProcess(SampledProcess Process, ProcessSeen Seen, string Tool);

/// <summary>What sampling the process table during a leg found.</summary>
/// <param name="Samples">Every reading, kept in full.</param>
/// <param name="Contenders">
/// Build tools outside the harness's own process tree whose command line names this leg's build
/// directory. A test run started by hand in a shared build directory while a gate ran turned a
/// green suite red, with four test processes live at once, and no lock can see such a process.
/// </param>
/// <param name="SharedResourceUsers">
/// Tools that share state outside any build directory, such as a per-user cache. A warning and
/// never a refusal: several legs legitimately run them at the same time.
/// </param>
/// <param name="Unreadable">Samples whose process table could not be read, with the reason.</param>
/// <param name="Limits">What sampling cannot see, stated so a clean report is not read as more than it is.</param>
public sealed record ContentionReport(
    IReadOnlyList<ProcessSample> Samples,
    IReadOnlyList<ContendingProcess> Contenders,
    IReadOnlyList<ContendingProcess> SharedResourceUsers,
    IReadOnlyList<string> Unreadable,
    IReadOnlyList<string> Limits)
{
    /// <summary>Whether another process used this leg's build directory while it ran.</summary>
    public bool Contended => Contenders.Count > 0;

    /// <summary>
    /// The verdict this forces on the leg, or <see langword="null"/> when nothing contended. No
    /// verdict here depends on a sample finishing within a time window: sampling costs seconds on
    /// one platform and a fraction of that on another, and one such overhead asymmetry was once
    /// read, for a whole cycle, as a speed difference between legs.
    /// </summary>
    public ReachedVerdict? Verdict()
        => Contended
            ? ReachedVerdict.Of(
                LegVerdict.Contended,
                $"{Contenders.Count} process(es) used the build directory: "
                + string.Join(", ", Contenders.Select(found => $"{found.Process.Name} (pid {found.Process.Id}, seen {Describe(found.Seen)})")))
            : null;

    /// <summary>The words the report uses for when a process was seen.</summary>
    /// <param name="seen">When it was seen.</param>
    public static string Describe(ProcessSeen seen) => seen switch
    {
        ProcessSeen.Throughout => "throughout",
        ProcessSeen.AtTheStart => "at the start",
        ProcessSeen.AtTheEnd => "at the end",
        _ => "during the run",
    };
}
