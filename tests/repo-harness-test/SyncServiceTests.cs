using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// A sync end to end, against a real tree and a real copy. The transport is the local one, which is
/// the same implementation a host runs on its own side, so what this proves holds there too.
/// </summary>
public sealed class SyncServiceTests
{
    [Fact]
    public async Task AFirstSync_CreatesTheCopy_AndItBecomesAGitRepository()
    {
        // Nothing creates a host's copy by hand any more: the first sync to a fresh host is what
        // has to work, or every leg needs a clone made by somebody.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var result = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.True(result.Created);
            Assert.True(result.Verified);
            Assert.True(File.Exists(Path.Combine(copy, "src", "a.c")));
            Assert.True(Directory.Exists(Path.Combine(copy, ".git")), "The copy is not a git repository.");
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    [Fact]
    public async Task ASecondSync_WritesOnlyWhatChanged_AndLeavesTheRestUntouched()
    {
        // An unchanged file that is rewritten gets a new modification time, and an incremental
        // build decides what is stale by ordering timestamps.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            var untouched = Path.Combine(copy, "src", "b.c");
            var before = File.GetLastWriteTimeUtc(untouched);

            await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "a.c"), "changed\n", cancellationToken);

            var second = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.Equal("src/a.c", Assert.Single(second.Plan.Writes).Path);
            Assert.Equal(before, File.GetLastWriteTimeUtc(untouched));
            Assert.Equal("changed\n", await File.ReadAllTextAsync(Path.Combine(copy, "src", "a.c"), cancellationToken));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    [Fact]
    public async Task AFileDeletedFromTheSource_IsDeletedFromTheCopy()
    {
        // A copy that only ever gains files is not a copy: a deleted source keeps compiling and a
        // deleted test keeps running, and the leg's verdict describes a tree that no longer exists.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);
            Assert.True(File.Exists(Path.Combine(copy, "src", "b.c")));

            File.Delete(Path.Combine(temp.Path, "src", "b.c"));

            var second = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.Equal("src/b.c", Assert.Single(second.Plan.Deletes));
            Assert.False(File.Exists(Path.Combine(copy, "src", "b.c")));
            Assert.True(second.Verified);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    [Fact]
    public async Task ABuildDirectoryInTheCopy_SurvivesASync()
    {
        // The never-transfer floor is also a never-delete floor. Without it the first sync after a
        // build deletes the build, and no incremental build on a host is ever possible.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            var artefact = Path.Combine(copy, "build", "x86_64-gcc-debug", "main.o");
            Directory.CreateDirectory(Path.GetDirectoryName(artefact)!);
            await File.WriteAllTextAsync(artefact, "object\n", cancellationToken);

            var second = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.Empty(second.Plan.Deletes);
            Assert.True(File.Exists(artefact), "The build directory was deleted from the copy.");
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    [Fact]
    public async Task ADirectoryTheHarnessDidNotCreate_IsNeverAdopted()
    {
        // Sync deletes whatever the source does not have, so adopting a checkout somebody made by
        // hand would delete work nothing here knows about, on a machine nobody is watching.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(copy);
            await File.WriteAllTextAsync(Path.Combine(copy, "someone-elses-work.txt"), "x\n", cancellationToken);

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("did not create it", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(copy, "someone-elses-work.txt")));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    [Fact]
    public async Task ADryRun_ChangesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var result = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(DryRun: true), cancellationToken);

            Assert.False(Directory.Exists(copy), "A dry run created the copy.");
            Assert.NotEmpty(result.Plan.Writes);
            Assert.Contains(result.Plan.Describe(SyncVerb.Planned), line => line.StartsWith("would write", StringComparison.Ordinal));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    [Fact]
    public async Task AnArtefactBroughtHome_IsCheckedOnArrival()
    {
        // Evidence that a binary built there runs here is not evidence if nobody checked it
        // survived the journey.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var landing = Path.Combine(temp.Path, "artefacts");

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            var produced = Path.Combine(copy, "out", "report.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(produced)!);
            await File.WriteAllTextAsync(produced, "measured\n", cancellationToken);

            var brought = await service.PullAsync(
                Transport(harness), copy, landing, ["out/report.txt"], cancellationToken);

            Assert.Equal("out/report.txt", Assert.Single(brought));
            Assert.Equal("measured\n", await File.ReadAllTextAsync(Path.Combine(landing, "out", "report.txt"), cancellationToken));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    [Fact]
    public async Task ALinkedFile_IsNeverTransferred_SoItsTargetsBytesNeverLeaveThisMachine()
    {
        // Opening a link reads whatever it points at. A link committed to the repository and aimed
        // outside it would have its target's bytes written to another machine under the link's own
        // name: the repository's content deciding what leaves this one.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var outside = Path.Combine(temp.Path, "..", "outside-" + Guid.NewGuid().ToString("N")[..8] + ".txt");

        try
        {
            await File.WriteAllTextAsync(outside, "a credential\n", cancellationToken);

            try
            {
                File.CreateSymbolicLink(Path.Combine(temp.Path, "src", "linked.c"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
            }

            var result = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.DoesNotContain(result.Plan.Writes, entry => entry.Path.EndsWith("linked.c", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(copy, "src", "linked.c")));
        }
        finally
        {
            DeleteIfPresent(copy);
            File.Delete(Path.Combine(temp.Path, "src", "linked.c"));
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("out/../../escape.txt")]
    public void APathLeavingTheTree_IsRefusedBeforeAnythingIsTouched(string path)
    {
        // The source of a path is a manifest the far side produced, and `..` is what turns a
        // deletion inside a copy into a deletion of whatever sits beside it.
        var refusal = Assert.Throws<HarnessException>(() => LocalSyncTransport.Resolve("/host/repo", path));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
    }

    private static ISyncTransport Transport(HarnessFactory harness)
        => new LocalSyncTransport(
            harness.FileSystem,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            harness.GitClient);

    private static async Task<(HarnessFactory Harness, ISyncService Service)> PrepareAsync(
        TempDirectory temp,
        CancellationToken cancellationToken)
    {
        var harness = new HarnessFactory();

        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "a.c"), "a\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "b.c"), "b\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".gitignore"), "build/\n", cancellationToken);

        await harness.InitializeHarnessAsync(temp.Path, cancellationToken, new HarnessConfig());
        await harness.CommitAllAsync(temp.Path, "initial", cancellationToken);

        // The host-facing collaborators are substitutes: every test here syncs into a directory on
        // this machine, which is the same transport a host runs on its own side, so nothing in these
        // tests should be able to reach a host by accident.
        var service = new SyncService(
            harness.ContextLoader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Transport(harness),
            Substitute.For<ISyncTransportFactory>(),
            new LegsService(harness.ContextLoader, Substitute.For<IHostInspector>(), harness.Output),
            harness.GitClient,
            harness.Output);

        return (harness, service);
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                new HarnessFactory().FileSystem.DeleteDirectory(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Current.AddWarning($"The sync copy at '{path}' could not be deleted: {ex.Message}");
        }
    }
}
