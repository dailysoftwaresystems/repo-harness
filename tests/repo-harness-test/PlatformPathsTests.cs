using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// How a path reads on each platform, from its text alone: one reading, which the validator and the
/// survey ask and the machine that starts the program shares, so none of them can come to disagree
/// about a path.
/// </summary>
public sealed class PlatformPathsTests
{
    [Theory]
    [InlineData("/opt/homebrew/bin/cmake", "macos", true)]
    [InlineData("/usr/bin/gcc-13", "linux", true)]
    [InlineData(@"C:\tools\cmake.exe", "windows", true)]
    [InlineData("C:/tools/cmake.exe", "windows", true)]
    [InlineData(@"\\server\share\cmake.exe", "windows", true)]
    [InlineData("//server/share/cmake.exe", "windows", true)]
    [InlineData(@"C:\tools\cmake.exe", "linux", false)]
    [InlineData("/opt/tools/cmake", "windows", false)]
    [InlineData("C:tools", "windows", false)]
    [InlineData("tools/cmake", "linux", false)]
    [InlineData("./cmake", "macos", false)]
    public void APath_IsAbsoluteOnlyWhereItsPlatformReadsItSo(string path, string platform, bool absolute)
        => Assert.Equal(absolute, PlatformPaths.IsAbsoluteOn(path, platform));

    /// <summary>
    /// Absolute somewhere is absolute on one platform or the other: what a setting read on any
    /// machine, for a machine it cannot tell, may be. '~/' is the home of whoever reads it, and only
    /// with its slash.
    /// </summary>
    [Theory]
    [InlineData("/opt/tools", true, false)]
    [InlineData(@"C:\tools", true, false)]
    [InlineData(@"\\server\share", true, false)]
    [InlineData("C:tools", false, false)]
    [InlineData("tools", false, false)]
    [InlineData("~/bin", false, true)]
    [InlineData("~bin", false, false)]
    [InlineData("~", false, false)]
    public void APath_IsAbsoluteSomewhere_OrFromTheHome_ByItsTextAlone(string path, bool absolute, bool home)
    {
        Assert.Equal(absolute, PlatformPaths.IsAbsoluteOnAnyPlatform(path));
        Assert.Equal(home, PlatformPaths.IsHomeRelative(path));
    }

    /// <summary>
    /// A program carrying a separator or a drive is a path, started where it points and looked for
    /// nowhere else; anything else is a name the PATH is asked about.
    /// </summary>
    [Theory]
    [InlineData("cmake", false)]
    [InlineData("cmake.exe", false)]
    [InlineData("./cmake", true)]
    [InlineData("tools/cmake", true)]
    [InlineData(@"tools\cmake", true)]
    [InlineData("C:cmake", true)]
    [InlineData(@"C:\tools\cmake.exe", true)]
    public void AProgramIsAPath_WhenItCarriesASeparatorOrADrive(string program, bool path)
        => Assert.Equal(path, ProcessRunner.IsPath(program));
}
