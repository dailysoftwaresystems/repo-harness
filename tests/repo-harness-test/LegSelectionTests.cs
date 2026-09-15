using RepoHarness.Core.Configuration;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>What --legs selects: every leg when it is left out, and exactly what it names otherwise.</summary>
public sealed class LegSelectionTests
{
    private static readonly HarnessConfig Config = new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Legs =
        {
            ["linux-x64"] = HostDoubles.Leg("linux", "x86_64"),
            ["linux-arm64"] = HostDoubles.Leg("linux", "arm64"),
            ["windows-x64"] = HostDoubles.Leg("windows", "x86_64"),
        },
        LegSets = { ["linux"] = ["linux-x64", "linux-arm64"] },
    };

    [Fact]
    public void LeavingLegsOut_SelectsEveryLeg_InTheOrderTheyAreDeclared()
    {
        var selection = LegSelection.Resolve(Config, null);

        Assert.False(selection.Named);
        Assert.Equal(["linux-x64", "linux-arm64", "windows-x64"], selection.Legs.Select(leg => leg.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData(" , |")]
    public void GivingLegsNoName_IsAUsageError_RatherThanEveryLeg(string values)
    {
        // An unset variable in --legs "$GATE" would otherwise check every leg under the rule for unnamed ones.
        var exception = Assert.Throws<HarnessException>(() => LegSelection.Resolve(Config, values.Split('|')));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Contains("--legs was given no leg or leg set name", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linux-x64,windows-x64")]
    [InlineData("linux-x64,|windows-x64")]
    [InlineData("linux-x64|windows-x64")]
    [InlineData(" linux-x64 , windows-x64 ")]
    public void Names_MayBeSeparatedByCommas_OrGivenSeparately(string values)
    {
        // '|' separates the values the command line would pass, so "a,|b" is --legs a, b.
        var selection = LegSelection.Resolve(Config, values.Split('|'));

        Assert.True(selection.Named);
        Assert.Equal(["linux-x64", "windows-x64"], selection.Legs.Select(leg => leg.Name));
    }

    [Fact]
    public void ALegSetsName_SelectsItsLegs_EachOnce_UnderTheirDeclaredNames()
    {
        var selection = LegSelection.Resolve(Config, ["LINUX", "Linux-X64"]);

        Assert.Equal(["linux-x64", "linux-arm64"], selection.Legs.Select(leg => leg.Name));
    }

    [Fact]
    public void AnUnknownName_IsAUsageError_ThatNamesIt()
    {
        var exception = Assert.Throws<HarnessException>(() => LegSelection.Resolve(Config, ["linux-x64,nope"]));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Contains("'nope'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("declared legs: linux-x64, linux-arm64, windows-x64", exception.Message, StringComparison.Ordinal);
    }
}
