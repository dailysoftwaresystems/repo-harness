using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// How paths compare is asked of the file system, never assumed from the operating system: macOS's
/// default volume finds a file by its name in any case, and Linux's do not.
/// </summary>
public sealed class PathCaseTests
{
    /// <summary>
    /// A file system that finds a directory by its name in another case compares paths ignoring
    /// case - asked in lower case of one named all in upper case - and one that does not compares
    /// them exactly, as does a path with no letter to change.
    /// </summary>
    [Fact]
    public void PathsCompare_AsTheFileSystemFindsADirectoryByItsNameInAnotherCase()
    {
        var folding = Substitute.For<IFileSystem>();
        folding.DirectoryExists(Arg.Any<string>()).Returns(true);

        var exact = Substitute.For<IFileSystem>();
        exact.DirectoryExists("/work/Build").Returns(true);

        var upper = Substitute.For<IFileSystem>();
        upper.DirectoryExists("/work/build").Returns(true);

        Assert.Same(StringComparer.OrdinalIgnoreCase, PathCase.In(folding, "/work/Build"));
        Assert.Same(StringComparer.OrdinalIgnoreCase, PathCase.In(upper, "/WORK/BUILD"));
        Assert.Same(StringComparer.Ordinal, PathCase.In(exact, "/work/Build"));
        Assert.Same(StringComparer.Ordinal, PathCase.In(folding, "/1/2"));
    }

    /// <summary>
    /// On this machine's own file system the answer is the one a file there gives: found by its name
    /// in another case on Windows and on macOS's default volume, and not on Linux.
    /// </summary>
    [Fact]
    public void ThisMachinesFileSystem_AnswersAsAFileOnItDoes()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("probe.txt", "probe");

        var folds = File.Exists(temp.Combine("PROBE.TXT"));

        Assert.Same(
            folds ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal,
            PathCase.In(new PhysicalFileSystem(FilePermissionsFactory.Create()), temp.Path));
    }
}
