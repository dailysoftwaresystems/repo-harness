using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>One unit a segment carried to an outcome.</summary>
/// <param name="Unit">The unit's identity, as the run attributes its output.</param>
/// <param name="Outcome">What it reported.</param>
/// <param name="CompletedAt">When it finished.</param>
public sealed record RunSegmentUnit(string Unit, RunOutcome Outcome, DateTimeOffset CompletedAt);

/// <summary>One attempt at a run: what it started, what it finished, and whether it ended.</summary>
/// <remarks>
/// A segment with no end is one that was interrupted. It is kept exactly as it was left: what it
/// completed before it stopped is measured work, and discarding it would make a long suite that
/// aborts near the end cost its whole duration again.
/// </remarks>
public sealed class RunSegment
{
    /// <summary>This attempt's id, unique within the run.</summary>
    public required string SegmentId { get; init; }

    /// <summary>When the attempt started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When it ended, or <see langword="null"/> when it was interrupted.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The units this attempt carried to an outcome, in the order it finished them.</summary>
    public List<RunSegmentUnit> Completed { get; init; } = [];
}

/// <summary>
/// Every segment of one run, and the union across them.
/// </summary>
/// <remarks>
/// The union is what a resumed run reports. A suite that ran two thirds of its units, aborted, and
/// finished the rest on a second invocation did the whole suite once; reporting only the second
/// invocation would report a third of it, and reporting the two separately would leave the reader
/// to add them up and to decide what a unit that appears twice means.
/// </remarks>
public sealed class RunSegmentRecord
{
    /// <summary>The run these segments belong to.</summary>
    public required string RunId { get; init; }

    /// <summary>Every attempt, oldest first.</summary>
    public List<RunSegment> Segments { get; init; } = [];

    /// <summary>
    /// The outcome of every unit any segment completed. A unit completed more than once keeps the
    /// latest outcome, because resume skips what is already done, so a second record of one unit
    /// means it was deliberately run again.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, RunOutcome> Union
    {
        get
        {
            var union = new Dictionary<string, RunOutcome>(StringComparer.OrdinalIgnoreCase);

            foreach (var completed in Segments.SelectMany(segment => segment.Completed))
            {
                union[completed.Unit] = completed.Outcome;
            }

            return union;
        }
    }

    /// <summary>Whether any segment carried <paramref name="unit"/> to an outcome.</summary>
    /// <param name="unit">The unit's identity.</param>
    public bool IsCompleted(string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        return Segments.Any(segment => segment.Completed.Any(
            completed => completed.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Of <paramref name="units"/>, those no segment has completed: what a resumed run still has
    /// to do.
    /// </summary>
    /// <param name="units">Every unit the run was asked for.</param>
    public IReadOnlyList<string> Remaining(IEnumerable<string> units)
    {
        ArgumentNullException.ThrowIfNull(units);

        return [.. units.Where(unit => !IsCompleted(unit))];
    }
}

/// <summary>
/// Keeps the record of what a run has already completed, so an aborted run is resumed rather than
/// repeated.
/// </summary>
/// <remarks>
/// The record lives beside the run's logs, under the run's own directory, so it travels with the
/// evidence it describes: a record kept elsewhere outlives the logs that would explain it, and a
/// resumed run then skips units whose output nobody can find.
/// </remarks>
public sealed class RunSegments(IFileSystem fileSystem, IHarnessOutput output)
{
    /// <summary>Name of the record, inside the run's own directory.</summary>
    public const string RecordFileName = "segments.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NewLine = "\n",
    };

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>Where one run's segment record lives.</summary>
    /// <param name="layout">Resolved paths for this repository.</param>
    /// <param name="runId">The run's id.</param>
    public static string RecordPath(HarnessLayout layout, string runId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return Path.Combine(layout.RunDirectory(runId), RecordFileName);
    }

    /// <summary>
    /// The record of <paramref name="runId"/>, or an empty one when the run has no segments yet.
    /// </summary>
    /// <param name="layout">Resolved paths for this repository.</param>
    /// <param name="runId">The run's id.</param>
    /// <exception cref="HarnessException">
    /// The record exists and could not be read. It is never treated as empty: that would silently
    /// repeat work already measured, and hide that the evidence of the first attempt is damaged.
    /// </exception>
    public RunSegmentRecord Load(HarnessLayout layout, string runId)
    {
        var path = RecordPath(layout, runId);

        if (!_fileSystem.FileExists(path))
        {
            return new RunSegmentRecord { RunId = runId };
        }

        try
        {
            return JsonSerializer.Deserialize<RunSegmentRecord>(
                _fileSystem.ReadAllText(path),
                SerializerOptions) ?? throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"'{path}' holds no segment record.");
        }
        catch (JsonException exception)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"'{path}' could not be read ({exception.Message}). "
                + "Delete it to run the whole suite again.",
                exception);
        }
    }

    /// <summary>
    /// Opens a new segment of <paramref name="runId"/> and returns the record it was added to.
    /// </summary>
    /// <param name="layout">Resolved paths for this repository.</param>
    /// <param name="runId">The run's id.</param>
    /// <param name="segmentId">This attempt's id, unique within the run.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    public Task<RunSegmentRecord> BeginAsync(
        HarnessLayout layout,
        string runId,
        string segmentId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        cancellationToken.ThrowIfCancellationRequested();

        var record = Load(layout, runId);

        if (record.Segments.Any(segment => segment.SegmentId.Equals(segmentId, StringComparison.Ordinal)))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"run '{runId}' already has a segment '{segmentId}'. Two attempts sharing one id "
                + "would record their work into each other.");
        }

        record.Segments.Add(new RunSegment { SegmentId = segmentId, StartedAt = startedAt });
        Save(layout, record);

        if (record.Segments.Count > 1)
        {
            var done = record.Union.Count;
            _output.Info("run", $"resuming run '{runId}': {done} unit(s) already completed");
        }

        return Task.FromResult(record);
    }

    /// <summary>
    /// Records that <paramref name="unit"/> reached <paramref name="outcome"/> in this segment.
    /// </summary>
    /// <remarks>
    /// Written as each unit finishes rather than once at the end. A run that aborts is exactly the
    /// case this record exists for, and a record written only on a clean exit is never written on
    /// the run that needed it.
    /// </remarks>
    /// <param name="layout">Resolved paths for this repository.</param>
    /// <param name="runId">The run's id.</param>
    /// <param name="segmentId">The open segment's id.</param>
    /// <param name="unit">The unit that finished.</param>
    /// <param name="outcome">What it reported.</param>
    /// <param name="completedAt">When it finished.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    public Task RecordCompletedAsync(
        HarnessLayout layout,
        string runId,
        string segmentId,
        string unit,
        RunOutcome outcome,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(outcome);
        cancellationToken.ThrowIfCancellationRequested();

        var record = Load(layout, runId);
        var segment = Segment(record, runId, segmentId);

        segment.Completed.Add(new RunSegmentUnit(unit, outcome, completedAt));
        Save(layout, record);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Closes this segment, so a later invocation can tell an interrupted attempt from a finished
    /// one.
    /// </summary>
    /// <param name="layout">Resolved paths for this repository.</param>
    /// <param name="runId">The run's id.</param>
    /// <param name="segmentId">The open segment's id.</param>
    /// <param name="endedAt">When the attempt ended.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    public Task EndAsync(
        HarnessLayout layout,
        string runId,
        string segmentId,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var record = Load(layout, runId);
        Segment(record, runId, segmentId).EndedAt = endedAt;
        Save(layout, record);

        return Task.CompletedTask;
    }

    private static RunSegment Segment(RunSegmentRecord record, string runId, string segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);

        return record.Segments.FirstOrDefault(
            segment => segment.SegmentId.Equals(segmentId, StringComparison.Ordinal))
            ?? throw new HarnessException(
                HarnessExit.InternalError,
                $"run '{runId}' has no segment '{segmentId}'; it was never opened.");
    }

    private void Save(HarnessLayout layout, RunSegmentRecord record)
    {
        var path = RecordPath(layout, record.RunId);

        _fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
        _fileSystem.WriteAllTextAtomic(path, JsonSerializer.Serialize(record, SerializerOptions));
    }
}
