using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>
/// Versions decide which side of a host is updated. Ordered wrongly, a host that is ahead would be
/// taken for one that is behind, and moved down.
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.2.3-beta", "1.2.3")]
    [InlineData("0.2.1", "0.2.2-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    public void Compare_OrdersVersionsAsNuGetDoes(string lower, string higher)
    {
        var first = Parse(lower);
        var second = Parse(higher);

        Assert.True(SemanticVersion.Compare(first, second) < 0, $"{lower} should sort before {higher}");
        Assert.True(SemanticVersion.Compare(second, first) > 0, $"{higher} should sort after {lower}");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc", "1.2.3")]
    [InlineData("1.2.3-BETA", "1.2.3-beta")]
    public void Compare_TreatsTheSameVersion_AsEqual(string first, string second)
    {
        Assert.Equal(0, SemanticVersion.Compare(Parse(first), Parse(second)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.x")]
    public void TryParse_RefusesWhatIsNotAVersion(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void ToString_LeavesOutBuildMetadata()
    {
        Assert.Equal("1.2.3-beta", Parse("1.2.3-beta+sha.abc").ToString());
    }

    private static SemanticVersion Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version), $"'{text}' did not parse");
        return version!;
    }
}
