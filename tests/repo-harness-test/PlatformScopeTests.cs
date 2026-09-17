using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// The one place a list- or map-shaped setting is narrowed to a platform, so what it decides is
/// pinned here rather than at each of the settings that trust it. A setting whose platforms are
/// named properties rather than keys — a project's <c>test</c> block — is merged field by field
/// elsewhere, which is a different problem than this solves.
/// </summary>
public sealed class PlatformScopeTests
{
    [Theory]
    [InlineData("windows", new[] { "windows" }, true)]
    [InlineData("linux", new[] { "windows" }, false)]
    [InlineData("linux", new[] { "linux", "windows" }, true)]
    [InlineData("macos", new[] { "all" }, true)]
    [InlineData("macos", new[] { "ALL" }, true)]
    [InlineData("WINDOWS", new[] { "windows" }, true)]
    public void Applies_AnswersWhetherAnEntryIsNeededHere(string platformKey, string[] platforms, bool expected)
        => Assert.Equal(expected, PlatformScope.Applies(platforms, platformKey));

    /// <summary>
    /// Naming no platform means every platform, not none. A list left out is how every entry
    /// written before platform scoping existed reads, and each of them applied everywhere.
    /// </summary>
    [Fact]
    public void Applies_TreatsAnEmptyListAsEveryPlatform()
    {
        Assert.True(PlatformScope.Applies([], "linux"));
        Assert.True(PlatformScope.Applies(null, "linux"));
    }

    /// <summary>
    /// A host nobody could measure needs everything rather than nothing: refusing what cannot be
    /// placed would quietly turn an unmeasured host into one that needs no tools at all.
    /// </summary>
    [Fact]
    public void Applies_WhenThePlatformIsUnknown()
    {
        Assert.True(PlatformScope.Applies(["windows"], null));
        Assert.True(PlatformScope.Applies(["windows"], string.Empty));
    }

    [Fact]
    public void Select_PrefersThePlatformsOwnEntry_OverTheGeneralOne()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["all"] = "bin/app",
            ["windows"] = "bin/app.exe",
        };

        Assert.Equal("bin/app.exe", PlatformScope.Select(map, "windows"));
        Assert.Equal("bin/app", PlatformScope.Select(map, "linux"));
    }

    [Fact]
    public void Select_FindsNothing_WhenNeitherThePlatformNorEveryIsDeclared()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["windows"] = "bin/app.exe" };

        Assert.Null(PlatformScope.Select(map, "linux"));
        Assert.False(PlatformScope.Covers(map, "linux"));
        Assert.True(PlatformScope.Covers(map, "windows"));
    }

    /// <summary>
    /// A map assembled in code need not carry a case-insensitive comparer, and a lookup that
    /// silently missed would read as a platform declaring nothing.
    /// </summary>
    [Fact]
    public void Select_IgnoresKeyCase_EvenOnAnOrdinalMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["Windows"] = "bin/app.exe" };

        Assert.Equal("bin/app.exe", PlatformScope.Select(map, "windows"));
    }

    [Fact]
    public void Select_AndCovers_AnswerForAnEmptyMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Assert.Null(PlatformScope.Select(map, "windows"));
        Assert.False(PlatformScope.Covers(map, "windows"));
    }
}
