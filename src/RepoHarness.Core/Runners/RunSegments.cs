using System.Buffers;
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
    /// <remarks>
    /// Set only by the store that owns the record, and read back through <see cref="JsonIncludeAttribute"/>
    /// because the setter is not public: the end of an attempt is the store's to record, and a
    /// caller that could set it would be deciding on its own that work it did not do was finished.
    /// </remarks>
    [JsonInclude]
    public DateTimeOffset? EndedAt { get; internal set; }

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

    /// <summary>The leg these segments belong to, which is what keeps one run's legs apart.</summary>
    public required string Leg { get; init; }

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
/// resumed run then skips units whose output nobody can find. That directory is in the tree the run
/// was started from, so a run is resumed from that same tree.
/// </remarks>
public sealed class RunSegments(IFileSystem fileSystem, IHarnessOutput output)
{
    /// <summary>What every leg's record file name starts with, inside the run's own directory.</summary>
    public const string RecordPrefix = "segments-";

    /// <summary>What every leg's record file name ends with.</summary>
    public const string RecordSuffix = ".json";

    /// <summary>
    /// Characters a name may keep when it becomes a file name: the ones every platform this tool
    /// runs on accepts. Parentheses are among them, because the runner itself puts them in the name
    /// of a step with several lines.
    /// </summary>
    private static readonly SearchValues<char> SafeCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_. ()");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NewLine = "\n",
    };

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>Where one leg's segment record lives, inside its run's directory.</summary>
    /// <param name="layout">Resolved paths for this repository.</param>
    /// <param name="runId">The run's id.</param>
    /// <param name="leg">The leg the record belongs to.</param>
    /// <remarks>
    /// Per leg, not per run. One run's legs run at once and are given one run id, and every leg of
    /// a runner walks the same step names: sharing a record, the second leg finds every step
    /// already recorded, skips all of them, and reports that it passed work another machine did.
    /// The logs are already named this way, for the same reason.
    /// </remarks>
    public static string RecordPath(HarnessLayout layout, string runId, string leg)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        return Path.Combine(layout.RunDirectory(runId), $"{RecordPrefix}{FileNameFor(leg)}{RecordSuffix}");
    }

    /// <summary>
    /// <paramref name="name"/> with everything a file name cannot safely carry replaced, so a leg
    /// or a step named after a path or a flag still gets a file of its own.
    /// </summary>
    /// <param name="name">The name to make safe.</param>
    public static string FileNameFor(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return string.Create(name.Length, name, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = SafeCharacters.Contains(source[index]) ? source[index] : '-';
            }
        });
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
    /// <param name="leg">The leg the record belongs to.</param>
    public RunSegmentRecord Load(HarnessLayout layout, string runId, string leg)
    {
        var path = RecordPath(layout, runId, leg);

        if (!_fileSystem.FileExists(path))
        {
            return new RunSegmentRecord { RunId = runId, Leg = leg };
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
    /// <param name="leg">The leg the record belongs to.</param>
    /// <param name="segmentId">This attempt's id, unique within the run.</param>
    /// <param name="startedAt">When the attempt started.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    public Task<RunSegmentRecord> BeginAsync(
        HarnessLayout layout,
        string runId,
        string leg,
        string segmentId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        cancellationToken.ThrowIfCancellationRequested();

        var record = Load(layout, runId, leg);

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
    /// <param name="leg">The leg the record belongs to.</param>
    /// <param name="segmentId">The open segment's id.</param>
    /// <param name="unit">The unit that finished.</param>
    /// <param name="outcome">What it reported.</param>
    /// <param name="completedAt">When it finished.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    public Task RecordCompletedAsync(
        HarnessLayout layout,
        string runId,
        string leg,
        string segmentId,
        string unit,
        RunOutcome outcome,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(outcome);
        cancellationToken.ThrowIfCancellationRequested();

        var record = Load(layout, runId, leg);
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
    /// <param name="leg">The leg the record belongs to.</param>
    /// <param name="segmentId">The open segment's id.</param>
    /// <param name="endedAt">When the attempt ended.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    public Task EndAsync(
        HarnessLayout layout,
        string runId,
        string leg,
        string segmentId,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var record = Load(layout, runId, leg);
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
        var path = RecordPath(layout, record.RunId, record.Leg);

        _fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);
        _fileSystem.WriteAllTextAtomic(path, JsonSerializer.Serialize(record, SerializerOptions));
    }
}
