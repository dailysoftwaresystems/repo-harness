using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A runner left to its own default runs serially on one host and on every core on another, and
/// legs stop being comparable. These pin where the number comes from and how it reaches the runner.
/// </summary>
public sealed class CoreCountsTests
{
    [Fact]
    public void Resolution_PrefersTheInvocation_ThenTheHost_ThenDefaults()
    {
        Assert.Equal(2, CoreCounts.Resolve(invocation: 2, host: 4, defaults: 8).Value);
        Assert.Equal("invocation", CoreCounts.Resolve(invocation: 2, host: 4, defaults: 8).Source);

        // A remote host rarely has the same core count as the machine that wrote the configuration.
        Assert.Equal(4, CoreCounts.Resolve(invocation: null, host: 4, defaults: 8).Value);
        Assert.Equal(8, CoreCounts.Resolve(invocation: null, host: null, defaults: 8).Value);

        var builtIn = CoreCounts.Resolve(invocation: null, host: null, defaults: null);
        Assert.Equal(HarnessDefaults.DefaultCores, builtIn.Value);
        Assert.Equal(6, builtIn.Value);
    }

    [Fact]
    public void ACountBelowOne_IsRefused()
    {
        var refusal = Assert.Throws<HarnessException>(() => CoreCounts.Resolve(invocation: 0, host: null, defaults: null));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("at least 1", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Arguments_SubstituteTheCount()
    {
        Assert.Equal(["-j", "6"], CoreCounts.Arguments(["-j", "{cores}"], 6, []));
        Assert.Equal(["--parallel=4"], CoreCounts.Arguments(["--parallel={cores}"], 4, null));
        Assert.Empty(CoreCounts.Arguments(null, 6, []));
    }

    [Theory]
    [InlineData("-j", "8")]
    [InlineData("-j8", null)]
    [InlineData("-j=8", null)]
    public void AnExplicitOptionInTheInvocation_StillWins(string first, string? second)
    {
        // Spliced-in arguments would contradict the one already there, and which of the two the
        // runner honours is its business, not something the harness could report.
        string[] args = second is null ? [first] : [first, second];

        Assert.Empty(CoreCounts.Arguments(["-j", "{cores}"], 6, args));
    }

    [Fact]
    public void AnUnrelatedOption_DoesNotLookLikeTheCoreOption()
    {
        Assert.Equal(["-j", "6"], CoreCounts.Arguments(["-j", "{cores}"], 6, ["--output-on-failure", "-R", "fast"]));
    }

    [Fact]
    public void Environment_SetsEveryNamedVariable()
    {
        var variables = CoreCounts.Environment(["CTEST_PARALLEL_LEVEL", "CMAKE_BUILD_PARALLEL_LEVEL"], 6);

        Assert.Equal("6", variables["CTEST_PARALLEL_LEVEL"]);
        Assert.Equal("6", variables["CMAKE_BUILD_PARALLEL_LEVEL"]);
        Assert.Empty(CoreCounts.Environment(null, 6));
    }
}
