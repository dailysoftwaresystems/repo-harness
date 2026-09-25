using System.Collections.Concurrent;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Keeps asking for an ssh host that is waking up - its name, then a connection - for as long as the window
/// its <c>wakeWaitSeconds</c> gives it, before the host is reported unavailable.
/// </summary>
/// <remarks>
/// A personal Mac reached by its mDNS name falls back asleep between commands, and answers again moments
/// later: a consumer saw a run skip it seconds after a check had reached it, three times in fifteen minutes,
/// and a retry about 20 seconds later always found it. Three quick lookups miss that; a window catches it.
/// Only what a waking host explains is asked again: a name that does not resolve yet, a connection nothing
/// took or that timed out. A key refused, or one the name is not known by, is the host answering, and is
/// never retried. Whatever ran out is kept for the half minute a name's answer is, so the other legs a
/// command places there do not each wait the whole window again.
/// </remarks>
/// <param name="lookup">How a name is looked up, asked afresh each time: an answer kept would miss the wake.</param>
/// <param name="clock">What measures the window: its timestamps, which only move forward, never its time of day.</param>
/// <param name="pollDelay">
/// How long to wait between tries. Passed in rather than fixed so that a test measures the trying and not
/// the waiting; production uses <see cref="DefaultPollDelay"/>.
/// </param>
public sealed class SshWakeWindow(INameLookup lookup, TimeProvider clock, TimeSpan pollDelay)
{
    /// <summary>
    /// The wait between tries: a few within the 20-30 seconds a consumer measured a dark-waking Mac taking to
    /// answer, and few enough that a host that stays asleep costs a handful of lookups, not a flood.
    /// </summary>
    public static readonly TimeSpan DefaultPollDelay = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<HostId, (string Refusal, long Kept)> _ranOut = new();

    private readonly INameLookup _lookup = lookup;
    private readonly TimeProvider _clock = clock;
    private readonly TimeSpan _pollDelay = pollDelay;

    /// <summary>
    /// Now, on the clock a window is measured by: one that only moves forward, at the pace time passes. Never the
    /// time of day, which a machine may set while a host wakes - as one that syncs its time does - and which then
    /// cut a window short, or drew it out by as much as the clock was set back.
    /// </summary>
    public long Now => _clock.GetTimestamp();

    /// <summary>How long it has been since <paramref name="then"/>, a <see cref="Now"/> read earlier.</summary>
    /// <param name="then">When it was.</param>
    public TimeSpan Since(long then) => _clock.GetElapsedTime(then);

    /// <summary>Why <paramref name="host"/>'s window ran out a moment ago, or <see langword="null"/>.</summary>
    /// <param name="host">The host.</param>
    public string? RanOut(HostId host)
        => _ranOut.TryGetValue(host, out var kept) && Since(kept.Kept) < HostAddressResolver.CacheLifetime ? kept.Refusal : null;

    /// <summary>Keeps <paramref name="refusal"/> as why <paramref name="host"/>'s window ran out, for as long as a name's answer is kept.</summary>
    /// <param name="host">The host.</param>
    /// <param name="refusal">Why it could not be reached, window and all.</param>
    public void Remember(HostId host, string refusal) => _ranOut[host] = (refusal, Now);

    /// <summary>
    /// Looks the name <paramref name="missed"/> missed up again, until it resolves or the window ends, and says
    /// how many lookups it took in all.
    /// </summary>
    /// <param name="missed">The resolution that found nothing, with the lookups it made.</param>
    /// <param name="started">When the window began, a <see cref="Now"/>.</param>
    /// <param name="window">How long it lasts.</param>
    /// <param name="cancellationToken">Stops the looking.</param>
    public async Task<AddressResolution> KeepResolvingAsync(AddressResolution missed, long started, TimeSpan window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(missed);

        var attempts = missed.Attempts;

        // Asked at once, then after each wait: a miss kept from earlier in this command is no lookup of now.
        while (Since(started) < window)
        {
            attempts++;

            if (await _lookup.LookupAsync(missed.Address, cancellationToken).ConfigureAwait(false) is { Count: > 0 } found)
            {
                return new AddressResolution(missed.Address, Resolved: true, attempts, HostAddressResolver.Preferred(found));
            }

            await WaitAsync(started, window, cancellationToken).ConfigureAwait(false);
        }

        return missed with { Attempts = attempts };
    }

    /// <summary>
    /// Runs <paramref name="probe"/> again while what it fails with is what a waking host explains, until it
    /// succeeds or the window ends, and says what the last try came to and how many there were.
    /// </summary>
    /// <param name="failed">What the first try came to.</param>
    /// <param name="started">When the window began, a <see cref="Now"/>.</param>
    /// <param name="window">How long it lasts.</param>
    /// <param name="probe">One try.</param>
    /// <param name="cancellationToken">Stops the trying.</param>
    public async Task<(ProcessResult Result, int Attempts)> KeepProbingAsync(
        ProcessResult failed,
        long started,
        TimeSpan window,
        Func<CancellationToken, Task<ProcessResult>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(probe);

        var result = failed;
        var attempts = 1;

        // A try that itself takes connectTimeoutSeconds is charged to the window when it returns: the last one
        // may end after the window does, and none starts once it has.
        while (!result.Succeeded && HostProbes.MayBeWaking(result) && Since(started) < window)
        {
            await WaitAsync(started, window, cancellationToken).ConfigureAwait(false);

            if (Since(started) >= window)
            {
                break;
            }

            attempts++;
            result = await probe(cancellationToken).ConfigureAwait(false);
        }

        return (result, attempts);
    }

    /// <summary>Waits the delay between tries, or what is left of the window where that is less.</summary>
    private Task WaitAsync(long started, TimeSpan window, CancellationToken cancellationToken)
    {
        var left = window - Since(started);

        return Task.Delay(left < _pollDelay ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : _pollDelay, cancellationToken);
    }
}
