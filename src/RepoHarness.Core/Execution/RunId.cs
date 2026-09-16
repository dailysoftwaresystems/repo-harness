using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace RepoHarness.Core.Execution;

/// <summary>
/// One run's identity, <c>yyyyMMdd-HHmmss-&lt;8 hex&gt;</c>, which every log path is scoped to so that
/// no two legs ever write one file and one leg's result can never be read as another's.
/// </summary>
/// <remarks>
/// The timestamp is for a reader; the eight hex characters are what make the id unique. One host
/// this tool must serve steps its wall clock forward by about 25 seconds every few seconds, so two
/// runs started a minute apart can carry the same second, and a run started after another can carry
/// an earlier one. <see cref="New"/> therefore takes its timestamp from a monotonic floor as well as
/// the clock, and the random part keeps two runs that begin in the same second apart regardless.
/// </remarks>
public sealed class RunId : IEquatable<RunId>
{
    /// <summary>How the timestamp part is spelled, in UTC.</summary>
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Characters of timestamp in an id, before the hyphen and the hex.</summary>
    private const int StampLength = 15;

    /// <summary>The length of a well-formed id: the timestamp, a hyphen, and eight hex characters.</summary>
    private const int IdLength = StampLength + 1 + 8;

    /// <summary>Where this process's monotonic floor starts: the wall clock when the type was first used.</summary>
    private static readonly DateTimeOffset MonotonicOrigin = DateTimeOffset.UtcNow;

    /// <summary>Monotonic time since <see cref="MonotonicOrigin"/>, which no clock step can move.</summary>
    private static readonly Stopwatch SinceOrigin = Stopwatch.StartNew();

    /// <summary>The latest timestamp already handed out, so a stepped-back clock cannot repeat one.</summary>
    private static long _floorTicks;

    private RunId(string value, DateTimeOffset startedUtc)
    {
        Value = value;
        StartedUtc = startedUtc;
    }

    /// <summary>
    /// The id as it is written: digits, hyphens and lowercase hex only, so it is a valid directory
    /// name on every platform and needs no escaping in a path, a log line or a JSON document.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// When the run started, in UTC, for display. Never compared with another timestamp to order
    /// anything: durations come from the monotonic clock.
    /// </summary>
    public DateTimeOffset StartedUtc { get; }

    /// <summary>A new id for a run starting now.</summary>
    public static RunId New()
    {
        var stamp = Monotonic();
        return new RunId(
            stamp.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "-" + RandomSuffix(),
            stamp);
    }

    /// <summary>
    /// The id <paramref name="text"/> spells.
    /// </summary>
    /// <param name="text">The id, as <see cref="Value"/> writes it.</param>
    /// <exception cref="FormatException">
    /// The text is not a run id. Refused rather than accepted as a directory name: an id read back
    /// from a path or an argument decides where logs are written, and anything else there would
    /// write them somewhere nobody named.
    /// </exception>
    public static RunId Parse(string text)
        => TryParse(text, out var runId)
            ? runId
            : throw new FormatException($"'{text}' is not a run id; the form is {TimestampFormat}-<8 hex>.");

    /// <summary>Reads <paramref name="text"/> back into an id, without throwing when it is not one.</summary>
    /// <param name="text">The candidate text.</param>
    /// <param name="runId">The id, when the text spells one.</param>
    /// <returns>Whether the text spelled an id.</returns>
    public static bool TryParse(string? text, out RunId runId)
    {
        runId = null!;

        if (text is not { Length: IdLength } || text[StampLength] != '-')
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                text[..StampLength],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var started))
        {
            return false;
        }

        foreach (var character in text.AsSpan(StampLength + 1))
        {
            // Uppercase is refused rather than accepted and lowered: two ids differing only in case
            // would be one directory on Windows and two on Linux.
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        runId = new RunId(text, started);
        return true;
    }

    /// <inheritdoc/>
    public bool Equals(RunId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RunId);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>The id as it is written.</summary>
    public override string ToString() => Value;

    /// <summary>
    /// Now, but never earlier than an id this process has already produced. The clock is read so
    /// that an id still says when the run began; the monotonic floor is what keeps a second id from
    /// carrying an earlier second than the first when the clock steps back between them.
    /// </summary>
    internal static DateTimeOffset Monotonic()
    {
        var monotonic = MonotonicOrigin + SinceOrigin.Elapsed;
        var candidate = DateTimeOffset.UtcNow;
        var ticks = Math.Max(candidate.UtcTicks, monotonic.UtcTicks);

        // Compare-and-swap rather than a lock: two legs asking at the same instant must each get an
        // id, and neither may get one that sorts before an id already handed out.
        while (true)
        {
            var floor = Interlocked.Read(ref _floorTicks);
            var next = Math.Max(ticks, floor);

            if (Interlocked.CompareExchange(ref _floorTicks, next, floor) == floor)
            {
                return new DateTimeOffset(next, TimeSpan.Zero);
            }
        }
    }

    /// <summary>
    /// Eight lowercase hex characters from the system's cryptographic source, which is the one
    /// source that does not repeat when two runs start in the same millisecond on the same machine.
    /// </summary>
    private static string RandomSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}
