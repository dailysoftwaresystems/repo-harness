using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Looks a name up with whatever this machine resolves names with: DNS, mDNS for a <c>.local</c> name,
/// or the hosts file. A seam rather than an abstraction over networking: it exists so that the retry
/// and the cache above it are testable without a LAN.
/// </summary>
public interface INameLookup
{
    /// <summary>The addresses <paramref name="name"/> resolves to, empty when it resolves to none.</summary>
    /// <param name="name">The name to look up.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INameLookup"/>
public sealed class DnsNameLookup : INameLookup
{
    public async Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, cancellationToken).ConfigureAwait(false);
            return [.. addresses.Select(address => address.ToString())];
        }
        catch (SocketException)
        {
            // A name that did not resolve is an answer, not a failure of this machine: the caller
            // retries, because one miss is normal for a name answered by the host itself.
            return [];
        }
    }
}

/// <summary>What resolving one host's address found.</summary>
/// <param name="Address">The address as the item declares it.</param>
/// <param name="Resolved">Whether it names a machine this one can reach.</param>
/// <param name="Attempts">How many lookups it took, so a name that needs retrying is visible.</param>
public sealed record AddressResolution(string Address, bool Resolved, int Attempts);

/// <summary>
/// Establishes that an ssh host's name resolves before ssh is started, retrying the lookup and keeping
/// the answer for a short while.
/// </summary>
/// <remarks>
/// Measured: a host on a DHCP LAN reached by an mDNS <c>.local</c> name fails a lookup as a matter of
/// course - the responder does not answer the first query and answers the second - and ssh then exits
/// with "Could not resolve hostname", which reads as a host that is switched off. Retrying here leaves
/// the answer in this machine's own resolver, so the lookup ssh makes a moment later hits it, and a
/// name that genuinely resolves to nothing is refused with that as the reason.
/// </remarks>
public interface IHostAddressResolver
{
    /// <summary>Resolves <paramref name="address"/>, retrying a miss.</summary>
    /// <param name="address">The address the host's item declares.</param>
    /// <param name="cancellationToken">Stops the lookups.</param>
    Task<AddressResolution> ResolveAsync(string address, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostAddressResolver"/>
/// <param name="lookup">How a name is looked up.</param>
/// <param name="clock">What decides when a cached answer has aged out.</param>
/// <param name="retryDelay">
/// How long to wait between lookups. Passed in rather than fixed so that a test measures the retrying
/// and not the waiting; production uses <see cref="DefaultRetryDelay"/>.
/// </param>
public sealed class HostAddressResolver(INameLookup lookup, TimeProvider clock, TimeSpan retryDelay) : IHostAddressResolver
{
    /// <summary>
    /// How many lookups a name gets before it is reported as resolving to nothing. Three, because the
    /// measured failure is a single miss of an mDNS query: one retry already covers it, and the third
    /// attempt covers a miss on a LAN busy enough to drop two.
    /// </summary>
    public const int Attempts = 3;

    /// <summary>
    /// The wait between lookups. Long enough for an mDNS responder to answer a repeated query, and
    /// short enough that three misses cost a second rather than a leg's worth of time.
    /// </summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long an answer is reused. One command measures a host several times over, and paying the
    /// same miss for each is exactly the failure this exists to end; half a minute is far shorter than
    /// any DHCP lease, so a machine that moved is still found again inside a single run.
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (bool Resolved, DateTimeOffset Until)> _answers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly INameLookup _lookup = lookup;
    private readonly TimeProvider _clock = clock;
    private readonly TimeSpan _retryDelay = retryDelay;

    public async Task<AddressResolution> ResolveAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        // A literal address resolves to itself; asking the resolver about one would fail on a machine
        // with no resolver at all, which is exactly the machine an address was written out for.
        if (IPAddress.TryParse(address, out _))
        {
            return new AddressResolution(address, Resolved: true, Attempts: 0);
        }

        if (_answers.TryGetValue(address, out var cached) && cached.Until > _clock.GetUtcNow())
        {
            return new AddressResolution(address, cached.Resolved, Attempts: 0);
        }

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }

            if ((await _lookup.LookupAsync(address, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                _answers[address] = (true, _clock.GetUtcNow() + CacheLifetime);
                return new AddressResolution(address, Resolved: true, attempt);
            }
        }

        // A miss is cached too, so that a command measuring several legs on one switched-off machine
        // does not pay three lookups for each of them.
        _answers[address] = (false, _clock.GetUtcNow() + CacheLifetime);
        return new AddressResolution(address, Resolved: false, Attempts);
    }

    /// <summary>What a host that does not resolve is reported as, with what to do about it.</summary>
    /// <param name="resolution">The failed resolution.</param>
    public static string Unresolved(AddressResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        return $"'{resolution.Address}' resolved to no address in "
            + $"{resolution.Attempts.ToString(CultureInfo.InvariantCulture)} lookups; check that the machine is on, "
            + "and that ADDRESS in its item's .env is the name this machine's network answers for";
    }
}
