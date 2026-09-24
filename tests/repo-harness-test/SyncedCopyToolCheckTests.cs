using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;

namespace RepoHarness.Tests;

/// <summary>
/// A command typed in a copy the harness synced to a host is told when the DssHarness running it trails the
/// newest release published: a host's DssHarness is brought to its dispatcher's version only when that
/// machine next reaches it, so a command typed there in between runs the old build and says what it said.
/// Every feed here is a double; no test reaches nuget.org.
/// </summary>
public sealed class SyncedCopyToolCheckTests
{
    [Fact]
    public async Task ACommandTypedInACopy_IsWarnedWhenTheToolRunningTrailsTheNewestRelease()
    {
        var fixture = new Fixture(running: "0.5.9", published: "0.5.10");

        await fixture.Check.WarnWhenBehindAsync(fixture.Copy, TestContext.Current.CancellationToken);

        var said = fixture.Error.ToString();
        Assert.Contains($"{ToolPackage.Command}: WARN - this tree is a copy the harness synced to a host", said, StringComparison.Ordinal);
        Assert.Contains($"the {ToolPackage.Id} running here is 0.5.9, while nuget.org has 0.5.10", said, StringComparison.Ordinal);
        Assert.Contains("run this from that machine", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing is said where the running build is the newest release, or ahead of it - a dispatcher that
    /// runs a prerelease brings its hosts to it, and that is ahead of every release.
    /// </summary>
    [Theory]
    [InlineData("0.5.10", "0.5.10")]
    [InlineData("0.5.11", "0.5.10")]
    [InlineData("0.6.0-beta", "0.5.10")]
    public async Task NothingIsSaid_WhereTheToolRunningIsTheNewestReleaseOrAhead(string running, string published)
    {
        var fixture = new Fixture(running, published);

        await fixture.Check.WarnWhenBehindAsync(fixture.Copy, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Feed.Asked);
        Assert.Empty(fixture.Error.ToString());
    }

    /// <summary>A host with no route to nuget.org is told nothing, rather than told something it cannot know.</summary>
    [Fact]
    public async Task NothingIsSaid_WhereNuGetOrgCannotBeReached()
    {
        var fixture = new Fixture(running: "0.5.9", published: null);

        await fixture.Check.WarnWhenBehindAsync(fixture.Copy, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Feed.Asked);
        Assert.Empty(fixture.Error.ToString());
    }

    /// <summary>A checkout somebody works in is where the dispatcher runs, and nuget.org is never asked there.</summary>
    [Fact]
    public async Task ACheckoutSomebodyWorksIn_NeverAsks()
    {
        var fixture = new Fixture(running: "0.5.9", published: "0.5.10");

        await fixture.Check.WarnWhenBehindAsync(fixture.Checkout, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.Feed.Asked);
        Assert.Empty(fixture.Error.ToString());
    }

    /// <summary>
    /// A command the host agent runs for another machine runs in the same copy, but after an inspection that
    /// has just made this build that machine's own: nothing to tell, and a leg would otherwise ask nuget.org
    /// once for every run.
    /// </summary>
    [Fact]
    public async Task ACommandServingAnotherMachine_NeverAsks()
    {
        var fixture = new Fixture(running: "0.5.9", published: "0.5.10", servesAnotherMachine: true);

        await fixture.Check.WarnWhenBehindAsync(fixture.Copy, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.Feed.Asked);
        Assert.Empty(fixture.Error.ToString());
    }

    /// <summary>A command loads its context more than once, and asks, and warns, once.</summary>
    [Fact]
    public async Task OneCommand_AsksOnce()
    {
        var fixture = new Fixture(running: "0.5.9", published: "0.5.10");

        await fixture.Check.WarnWhenBehindAsync(fixture.Copy, TestContext.Current.CancellationToken);
        await fixture.Check.WarnWhenBehindAsync(fixture.Copy, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Feed.Asked);
        Assert.Single(fixture.Error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A build whose version cannot be read - one from source - has nothing to compare, and asks nothing.</summary>
    [Fact]
    public async Task ARunningVersionThatCannotBeRead_NeverAsks()
    {
        var fixture = new Fixture(running: "dev", published: "0.5.10");

        await fixture.Check.WarnWhenBehindAsync(fixture.Copy, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.Feed.Asked);
        Assert.Empty(fixture.Error.ToString());
    }

    /// <summary>Under --verbose the asking is said, since it can cost the command a pause nothing else explains.</summary>
    [Fact]
    public async Task TheAsking_IsSaidUnderVerbose()
    {
        var fixture = new Fixture(running: "0.5.9", published: null, verbose: true);

        await fixture.Check.WarnWhenBehindAsync(fixture.Copy, TestContext.Current.CancellationToken);

        var said = fixture.Output.ToString();
        Assert.Contains($"nuget.org is asked which {ToolPackage.Id} is newest", said, StringComparison.Ordinal);
        Assert.Contains($"nuget.org did not say which {ToolPackage.Id} is newest, so 0.5.9 is not compared", said, StringComparison.Ordinal);
    }

    /// <summary>Versions are ordered as versions: read as text, 0.5.9 would be newer than 0.5.10.</summary>
    [Fact]
    public void TheNewestRelease_IsOrderedAsAVersion_NotAsText()
        => Assert.Equal("0.5.10", NuGetPublishedToolVersions.NewestRelease("""{"versions":["0.5.2","0.5.10","0.5.9"]}""")?.ToString());

    /// <summary>A prerelease is passed over, ahead of the newest release though it sorts.</summary>
    [Fact]
    public void APrerelease_IsPassedOver()
        => Assert.Equal("0.5.10", NuGetPublishedToolVersions.NewestRelease("""{"versions":["0.5.10","0.5.11-beta"]}""")?.ToString());

    /// <summary>An answer that is not the list it should be, or lists no release, is no answer at all.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"versions":"0.5.10"}""")]
    [InlineData("""{"versions":[]}""")]
    [InlineData("""{"versions":[1,null,"dev","0.5.11-beta"]}""")]
    public void AnAnswerListingNoRelease_IsNoAnswer(string json)
        => Assert.Null(NuGetPublishedToolVersions.NewestRelease(json));

    private sealed class Fixture
    {
        public Fixture(string running, string? published, bool servesAnotherMachine = false, bool verbose = false)
        {
            Feed = new PublishedVersionsDouble(published);
            Check = new SyncedCopyToolCheck(
                Feed,
                new RunningToolDouble(running),
                new CommandOrigin(servesAnotherMachine),
                new ConsoleHarnessOutput(Output, Error, verbose));

            var layout = new HarnessLayout(TestHost.TemporaryRoot, TestHost.TemporaryRoot);
            Checkout = new HarnessContext(layout, new HarnessConfig());
            Copy = Checkout with { IsSyncedCopy = true };
        }

        public PublishedVersionsDouble Feed { get; }

        public SyncedCopyToolCheck Check { get; }

        public StringWriter Output { get; } = new();

        public StringWriter Error { get; } = new();

        /// <summary>A checkout somebody works in.</summary>
        public HarnessContext Checkout { get; }

        /// <summary>A copy the harness synced to a host.</summary>
        public HarnessContext Copy { get; }
    }
}
