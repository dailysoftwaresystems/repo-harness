using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

public sealed class PhysicalFileSystemTests
{
    private static PhysicalFileSystem Create() => new(FilePermissionsFactory.Create());

    [Fact]
    public void WriteAllTextAtomic_WritesUtf8WithoutAByteOrderMark_CreatingParentDirectories()
    {
        // A byte order mark at the start of config.json or .gitignore is a stray
        // character to every tool that does not expect one.
        using var temp = new TempDirectory();
        var path = temp.Combine("nested", "deeper", "config.json");

        Create().WriteAllTextAtomic(path, "ação");

        Assert.Equal(Encoding.UTF8.GetBytes("ação"), File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAllTextAtomic_ReplacesAnExistingFile_AndLeavesNoTemporaryFileBehind()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("config.json", "old");

        Create().WriteAllTextAtomic(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task WriteAllTextAtomic_SurvivesConcurrentWriters()
    {
        // Two runs initialising one repository race exactly like this. No writer may fail,
        // and the file must end up as one writer's whole content, never a mixture.
        using var temp = new TempDirectory();
        var path = temp.Combine("config.json");
        var fileSystem = Create();
        var contents = Enumerable.Range(0, 8).Select(index => new string((char)('a' + index), 8192)).ToArray();

        await Task.WhenAll(contents.Select(content => Task.Run(
            () =>
            {
                for (var round = 0; round < 5; round++)
                {
                    fileSystem.WriteAllTextAtomic(path, content);
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Contains(File.ReadAllText(path), contents);
        Assert.Equal([path], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void DeleteDirectory_RemovesReadOnlyFiles()
    {
        // git marks its object files read only, and Windows refuses to delete those.
        using var temp = new TempDirectory();
        var tree = temp.Combine("tree");
        var file = temp.WriteFile(Path.Combine("tree", "objects", "ab", "cdef"), "blob");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        Create().DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void DeleteDirectory_DoesNothing_WhenTheDirectoryIsAbsent()
    {
        using var temp = new TempDirectory();

        Create().DeleteDirectory(temp.Combine("absent"));

        Assert.True(Directory.Exists(temp.Path));
    }

    /// <summary>
    /// A walk of a tree never descends through a directory link: one leads out of the tree, to files
    /// it does not contain, and one leads back into it, round and round. Both are here - a link out,
    /// and a link to the tree's own root - and the walk lists the tree's own file and ends. The links
    /// it passed over are exactly the ones a caller who has to say so is told about.
    /// </summary>
    [Fact]
    public void EnumerateFiles_Recursive_NeverDescendsThroughADirectoryLink_AndTheLinksAreNamed()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("outside", "elsewhere.txt"), "x");
        var own = temp.WriteFile(Path.Combine("tree", "sub", "own.txt"), "x");
        var tree = temp.Combine("tree");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(tree, "out"), temp.Combine("outside"));
            Directory.CreateSymbolicLink(Path.Combine(tree, "sub", "loop"), tree);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        var files = Create().EnumerateFiles(tree, recursive: true).ToList();
        var links = Create().EnumerateDirectoryLinks(tree).Order(StringComparer.Ordinal).ToList();

        Assert.Equal([own], files);
        Assert.Equal([Path.Combine(tree, "out"), Path.Combine(tree, "sub", "loop")], links);
    }

    /// <summary>
    /// The walk that dates what it finds reads each file's time as the file system keeps it, walks
    /// where the plain walk walks and nowhere else, and dates a link as itself: a link into the tree
    /// from outside it, made now to a file dated a day ahead, is dated now.
    /// </summary>
    [Fact]
    public void EnumerateWrittenFiles_DatesEachFile_WalkingNoDirectoryLink_AndALinkAsItself()
    {
        using var temp = new TempDirectory();
        var target = temp.WriteFile(Path.Combine("outside", "ahead.txt"), "x");
        var own = temp.WriteFile(Path.Combine("tree", "sub", "own.txt"), "x");
        var tree = temp.Combine("tree");
        var ownTime = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(own, ownTime);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(1));

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(tree, "out"), temp.Combine("outside"));
            File.CreateSymbolicLink(Path.Combine(tree, "linked.txt"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        var written = Create().EnumerateWrittenFiles(tree).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();

        Assert.Equal([Path.Combine(tree, "linked.txt"), own], written.Select(file => file.Path));
        Assert.True(written[0].LastWriteTimeUtc < DateTime.UtcNow.AddHours(1), $"the link was dated {written[0].LastWriteTimeUtc:O}, as what it points at");
        Assert.Equal(ownTime, written[1].LastWriteTimeUtc);
    }

    /// <summary>
    /// A file the walk listed but could not stat is asked again directly, and one gone since the walk
    /// listed it - its directory gone too, or not - is left out rather than failing the walk: on Linux and
    /// macOS a file removed between the listing and the stat is exactly that, and one deleted at the wrong
    /// moment left a build nothing to date its changes against.
    /// </summary>
    [Fact]
    public void Dated_AsksAgainAFileTheWalkCouldNotStat_AndLeavesOutOneGoneSince()
    {
        using var temp = new TempDirectory();
        var kept = temp.WriteFile("kept.txt", "x");
        var keptTime = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var unstatted = DateTime.FromFileTimeUtc(0);

        File.SetLastWriteTimeUtc(kept, keptTime);

        var dated = Create().Dated(
        [
            new WrittenFile(temp.Combine("gone.txt"), unstatted),
            new WrittenFile(kept, unstatted),
            new WrittenFile(temp.Combine("gone", "scratch.o"), unstatted),
        ]).ToList();

        Assert.Equal([new WrittenFile(kept, keptTime)], dated);
    }

    /// <summary>
    /// Asked when a path holding no file was last written, it raises rather than answering: the runtime
    /// answers 1601 for nothing there, which reads as a real time, and its own time for a directory, which
    /// is no file's. A file's is its own.
    /// </summary>
    [Fact]
    public void LastWriteTimeUtc_RaisesForAPathHoldingNoFile_NothingThereOrADirectory()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("file.txt", "x");
        var written = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        File.SetLastWriteTimeUtc(file, written);
        Directory.CreateDirectory(temp.Combine("folder"));

        Assert.Equal(written, Create().LastWriteTimeUtc(file));
        Assert.Throws<FileNotFoundException>(() => Create().LastWriteTimeUtc(temp.Combine("absent.txt")));
        Assert.Throws<FileNotFoundException>(() => Create().LastWriteTimeUtc(temp.Combine("folder")));
    }

    /// <summary>
    /// A file that is there, and whose time cannot be read - in a directory that can be listed but not
    /// searched, or read not at all - raises rather than being left out as gone: nothing says it is. A name
    /// starting with '.', hidden on Linux and macOS as a build directory's .ninja_log is, is no different.
    /// </summary>
    [Theory]
    [InlineData("listed.txt", UnixFileMode.UserRead)]
    [InlineData(".ninja_log", UnixFileMode.UserRead)]
    [InlineData("listed.txt", UnixFileMode.UserWrite)]
    [UnsupportedOSPlatform("windows")]
    public void Dated_RaisesForAFileThatIsThere_WhoseTimeCannotBeRead(string name, UnixFileMode mode)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no directory that can be listed and not searched.");

        using var temp = new TempDirectory();
        var file = temp.WriteFile(Path.Combine("unsearchable", name), "x");
        var directory = temp.Combine("unsearchable");

        File.SetUnixFileMode(directory, mode);

        try
        {
            Assert.SkipWhen(File.Exists(file), "This user reads past a directory's permissions.");

            var raised = Record.Exception(() => Create().Dated([new WrittenFile(file, DateTime.FromFileTimeUtc(0))]).ToList());

            Assert.True(raised is IOException or UnauthorizedAccessException, $"raised {raised?.GetType().Name ?? "nothing"}");
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A tree with no link in it has none to name.</summary>
    [Fact]
    public void EnumerateDirectoryLinks_NamesNothing_WhereThereIsNoLink()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("tree", "sub", "own.txt"), "x");

        Assert.Empty(Create().EnumerateDirectoryLinks(temp.Combine("tree")));
    }

    [Fact]
    public void DeleteDirectory_RemovesALink_WithoutTouchingWhatItPointsAt()
    {
        using var temp = new TempDirectory();
        var outside = temp.WriteFile(Path.Combine("outside", "keep.txt"), "keep");
        File.SetAttributes(outside, File.GetAttributes(outside) | FileAttributes.ReadOnly);

        // A read-only file inside the tree forces the path that clears attributes, which is
        // where following the link would reach the target.
        var tree = temp.Combine("tree");
        var locked = temp.WriteFile(Path.Combine("tree", "locked.txt"), "x");
        File.SetAttributes(locked, File.GetAttributes(locked) | FileAttributes.ReadOnly);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(tree, "link"), temp.Combine("outside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        Create().DeleteDirectory(tree);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(outside), "Deleting the tree deleted a file outside it.");
        Assert.True(
            File.GetAttributes(outside).HasFlag(FileAttributes.ReadOnly),
            "Deleting the tree changed a file outside it.");
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ProtectSecretFile_LeavesOnlyOwnerReadAndWrite_OnLinuxAndMacOs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows protects files with access lists; see the Windows test.");

        using var temp = new TempDirectory();
        var path = temp.WriteFile(".secret", "key");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Create().ProtectSecretFile(path);

        // ssh refuses a private key that anyone but its owner can read.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ProtectSecretFile_GrantsOnlyTheCurrentUser_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux and macOS protect files with mode bits; see that test.");

        using var temp = new TempDirectory();
        var path = temp.WriteFile(".secret", "key");

        Create().ProtectSecretFile(path);

        var security = new FileInfo(path).GetAccessControl();
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        using var identity = WindowsIdentity.GetCurrent();

        // Inheritance would keep whatever the directory grants, which is what this removes.
        Assert.True(security.AreAccessRulesProtected, "The file still inherits its directory's permissions.");
        var rule = Assert.Single(rules);
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
    }

    [Fact]
    public void ProtectSecretFile_Throws_ForAMissingFile()
    {
        using var temp = new TempDirectory();

        Assert.Throws<FileNotFoundException>(() => Create().ProtectSecretFile(temp.Combine("absent")));
    }

    [Fact]
    public void FilePermissions_MatchTheRunningPlatform()
    {
        // The expectation comes from RuntimeInformation, not from the code under test.
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? nameof(WindowsFilePermissions)
            : nameof(PosixFilePermissions);

        Assert.Equal(expected, FilePermissionsFactory.Create().GetType().Name);
    }

    [Fact]
    public void IsExecutable_IsFalse_ForAMissingFile()
    {
        using var temp = new TempDirectory();

        Assert.False(FilePermissionsFactory.Create().IsExecutable(temp.Combine("absent")));
    }
}
