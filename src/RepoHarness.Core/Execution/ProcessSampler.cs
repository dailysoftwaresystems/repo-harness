using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Execution;

/// <summary>What one leg wants the process table watched for.</summary>
public sealed record ContentionRequest
{
    /// <summary>The leg, so the report says whose build directory was used.</summary>
    public required string Leg { get; init; }

    /// <summary>
    /// The leg's build directory. A process is a contender only when its command line names this,
    /// because two legs with their own variant directories legitimately build at the same time.
    /// </summary>
    public required string BuildDirectory { get; init; }

    /// <summary>Tools from <c>contention.buildTools</c>, which make the verdict <c>contended</c>.</summary>
    public IReadOnlyList<string> BuildTools { get; init; } = [];

    /// <summary>Tools from <c>contention.sharedResourceTools</c>, which are reported as a warning.</summary>
    public IReadOnlyList<string> SharedResourceTools { get; init; } = [];

    /// <summary>Seconds between samples, beyond the ones always taken at the start and at the end.</summary>
    public int SampleSeconds { get; init; }

    /// <summary>
    /// Every other leg this machine could build, with its build directory, so work found beside this
    /// leg can be said to be that leg's rather than a stranger's.
    /// </summary>
    public IReadOnlyDictionary<string, string> OtherLegs { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Samples the machine's process table while a leg runs, and says what else was using the leg's
/// build directory.
/// </summary>
/// <remarks>
/// Machine-wide, not limited to this process's tree, because the hazard is a tool somebody started
/// by hand: the lock keeps two harness runs apart, and a lock cannot see a process that never took
/// one. Measured: a test run started in a shared build directory while a gate ran turned a green
/// suite red, with four test processes live at once.
/// </remarks>
public sealed class ProcessSampler(IProcessTable processTable, IHostPlatform platform, IHarnessOutput output)
{
    /// <summary>What the report always says sampling cannot see.</summary>
    /// <remarks>
    /// Stated on every report, including a clean one. A reader who does not know what was not
    /// looked at reads "no contender" as "nothing could have contended".
    /// </remarks>
    public static IReadOnlyList<string> KnownBlindSpots { get; } =
    [
        "a tool started from inside the build directory by a relative path is not seen, because another process's working directory cannot be read",
        "a process that does not expose its command line is counted but never matched against the build directory",
    ];

    /// <summary>How far a parent link is followed before the walk is abandoned as a cycle.</summary>
    private const int MaxAncestors = 64;

    private readonly IProcessTable _processTable = processTable;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// Reads the machine's process table once. A reading that failed comes back as a sample saying
    /// why, never as a sample holding nothing.
    /// </summary>
    /// <param name="index">Which reading this is.</param>
    /// <param name="elapsed">Monotonic time since the leg started.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<ProcessSample> SampleAsync(int index, TimeSpan elapsed, CancellationToken cancellationToken = default)
    {
        try
        {
            var reading = await _processTable.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (reading.Degraded is { } degraded)
            {
                // A reading that came back without command lines is reported as a reading that
                // failed, not as one that found nothing: contention is decided by a command line,
                // so this sample can no longer answer the question it was taken for.
                _output.Detail("sample", $"the process table could not be read: {degraded}");
            }

            return new ProcessSample(index, elapsed, reading.Processes, reading.Degraded);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or Win32Exception or NotSupportedException or ProgramStartException)
        {
            _output.Detail("sample", $"the process table could not be read: {ex.Message}");
            return new ProcessSample(index, elapsed, [], ex.Message);
        }
    }

    /// <summary>
    /// Starts sampling for a leg, taking the first reading before returning so the report always
    /// has one from before the work began.
    /// </summary>
    /// <param name="request">What to watch for.</param>
    /// <param name="cancellationToken">Stops the first reading; the session's own stop ends the rest.</param>
    public async Task<ProcessSamplingSession> StartAsync(ContentionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var first = await SampleAsync(0, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        return new ProcessSamplingSession(this, request, first, _platform.PathComparison);
    }

    /// <summary>
    /// Whether <paramref name="process"/> is inside the harness's own process tree, following a
    /// parent link only when the parent started no later than the child. A recycled id otherwise
    /// makes an unrelated process look like the harness's own child, and its contention invisible.
    /// </summary>
    /// <param name="process">The process to place.</param>
    /// <param name="byId">Every process in the same sample, by id.</param>
    /// <param name="harnessId">This process's id.</param>
    internal static bool InHarnessTree(SampledProcess process, IReadOnlyDictionary<int, SampledProcess> byId, int harnessId)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(byId);

        var current = process;

        for (var depth = 0; depth < MaxAncestors; depth++)
        {
            if (current.Id == harnessId)
            {
                return true;
            }

            if (current.ParentId is not { } parentId || parentId <= 0 || !byId.TryGetValue(parentId, out var parent))
            {
                return false;
            }

            if (parent.StartedUtc is { } parentStart && current.StartedUtc is { } childStart && parentStart > childStart)
            {
                // The "parent" started after its child, so the id has been reused since. The link
                // says nothing about this process, and following it would invent an ancestry.
                return false;
            }

            current = parent;
        }

        return false;
    }

}
