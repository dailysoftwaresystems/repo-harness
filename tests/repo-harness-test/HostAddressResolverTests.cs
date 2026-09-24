using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>
/// A host reached by an mDNS name on a DHCP LAN misses a lookup as a matter of course, and ssh then
/// exits saying the name could not be resolved, which reads as a machine that is switched off. One
/// miss must never end a leg.
/// </summary>
public sealed class HostAddressResolverTests
{
    private const string Name = "build-box.local";

    [Fact]
    public async Task AMissedLookup_IsRetried_AndTheHostIsStillReached()
    {
        var lookup = new ScriptedLookup(attempt => attempt >= 2 ? ["192.0.2.10"] : []);
        var resolver = Resolver(lookup);

        var resolution = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.True(resolution.Resolved);
        Assert.Equal(2, resolution.Attempts);
    }

    [Fact]
    public async Task ANameThatResolvesToNothing_IsRefused_AfterEveryAttempt()
    {
        var lookup = new ScriptedLookup(_ => []);
        var resolver = Resolver(lookup);

        var resolution = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.False(resolution.Resolved);
        Assert.Equal(HostAddressResolver.Attempts, resolution.Attempts);
        Assert.Equal(HostAddressResolver.Attempts, lookup.Calls);
        Assert.Contains("resolved to no address in 3 lookups", HostAddressResolver.Unresolved(resolution), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswer_IsKeptBriefly_SoOneCommandDoesNotPayTheSameMissForEveryProbe()
    {
        var clock = new SteppedClock();
        var lookup = new ScriptedLookup(_ => ["192.0.2.10"]);
        var resolver = new HostAddressResolver(lookup, clock, TimeSpan.Zero);

        _ = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.True(second.Resolved);
        Assert.Equal(0, second.Attempts);
        Assert.Equal(1, lookup.Calls);

        // Past the lifetime it is looked up again, so a machine whose lease moved is found where it is.
        clock.Advance(HostAddressResolver.CacheLifetime + TimeSpan.FromSeconds(1));
        _ = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        Assert.Equal(2, lookup.Calls);
    }

    [Fact]
    public async Task AMissIsKeptToo_SoASwitchedOffMachineIsNotLookedUpForEveryLeg()
    {
        var lookup = new ScriptedLookup(_ => []);
        var resolver = Resolver(lookup);

        _ = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        _ = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);

        Assert.Equal(HostAddressResolver.Attempts, lookup.Calls);
    }

    [Fact]
    public async Task ALiteralAddress_IsNeverLookedUp()
    {
        // A machine with no resolver at all is exactly the machine an address was written out for.
        var lookup = new ScriptedLookup(_ => throw new InvalidOperationException("A literal address was looked up."));

        var resolution = await Resolver(lookup).ResolveAsync("192.0.2.10", TestContext.Current.CancellationToken);

        Assert.True(resolution.Resolved);
        Assert.Equal(0, lookup.Calls);
    }

    /// <summary>
    /// A name resolves to an IPv4 address where it has one, since a link-local IPv6 one reaches the machine
    /// only through the interface its scope names, and a literal to itself; one kept briefly keeps its
    /// address, and a name that resolved to none has none.
    /// </summary>
    [Theory]
    [InlineData(new[] { "fe80::1%12", "192.0.2.10" }, "192.0.2.10")]
    [InlineData(new[] { "fe80::1%12" }, "fe80::1%12")]
    [InlineData(new string[0], null)]
    public async Task ANameResolvesToAnIpv4AddressFirst_AndALiteralToItself(string[] addresses, string? resolvedTo)
    {
        var lookup = new ScriptedLookup(_ => addresses);
        var resolver = Resolver(lookup);

        var first = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var kept = await resolver.ResolveAsync(Name, TestContext.Current.CancellationToken);
        var literal = await resolver.ResolveAsync("198.51.100.7", TestContext.Current.CancellationToken);

        Assert.Equal(resolvedTo, first.ResolvedTo);
        Assert.Equal(resolvedTo, kept.ResolvedTo);
        Assert.Equal(0, kept.Attempts);
        Assert.Equal("198.51.100.7", literal.ResolvedTo);
    }

    private static HostAddressResolver Resolver(INameLookup lookup)
        => new(lookup, new SteppedClock(), TimeSpan.Zero);

    /// <summary>Answers each lookup by its number, so a miss followed by a hit is exactly expressible.</summary>
    private sealed class ScriptedLookup(Func<int, IReadOnlyList<string>> answer) : INameLookup
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer(Calls));
        }
    }

    /// <summary>A clock that moves only when a test moves it, so a cache lifetime is tested in no time at all.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
