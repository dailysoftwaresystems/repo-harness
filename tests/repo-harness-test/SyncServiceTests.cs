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

    /// <summary>
    /// The refusal has to answer the question its reader asks next, which is what taking the
    /// directory over would cost. Before, it could not: the refusal came before any manifest was
    /// built, so nothing had worked out what was in there.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheHarnessDidNotCreate_IsRefused_NamingWhatAdoptingWouldCost()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // A checkout somebody made by hand: one file the source never had, and one the source
            // has too, holding an edit nobody committed.
            Directory.CreateDirectory(Path.Combine(copy, "src"));
            await File.WriteAllTextAsync(Path.Combine(copy, "stray.txt"), "x\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "a.c"), "edited and never committed\n", cancellationToken);

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);

            // The edit is named as an overwrite, which is the half that reads like an ordinary write
            // everywhere else, and the stray file as a deletion.
            Assert.Contains("overwrite src/a.c", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("delete    stray.txt", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("--adopt", refusal.Message, StringComparison.Ordinal);

            // Refused means refused: nothing there was touched.
            Assert.True(File.Exists(Path.Combine(copy, "stray.txt")));
            Assert.Equal(
                "edited and never committed\n",
                await File.ReadAllTextAsync(Path.Combine(copy, "src", "a.c"), cancellationToken),
                StringComparer.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// Taking it over is one flag, and what it must not cost is a warm build directory: git ignores
    /// it in the source, so it is withheld from the transfer and protected from the deletion alike.
    /// That is what makes adopting a hand-made checkout cheaper than moving it aside and rebuilding.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheHarnessDidNotCreate_IsTakenOverBy_Adopt_KeepingWhatGitIgnores()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(Path.Combine(copy, "src"));
            Directory.CreateDirectory(Path.Combine(copy, "build"));
            await File.WriteAllTextAsync(Path.Combine(copy, "stray.txt"), "x\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "a.c"), "stale\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "build", "warm.o"), "object\n", cancellationToken);

            var result = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: true), cancellationToken);

            Assert.True(result.Verified);

            // The tree now matches the source, and the hours of build output are still there.
            Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(copy, "src", "a.c"), cancellationToken), StringComparer.Ordinal);
            Assert.False(File.Exists(Path.Combine(copy, "stray.txt")));
            Assert.True(File.Exists(Path.Combine(copy, "build", "warm.o")));

            // Adopted for good: a second sync no longer has anything to refuse.
            var again = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.True(again.Plan.IsUpToDate);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A preview changes nothing, so there is nothing for either refusal to protect. Until now it hit
    /// both of them, including the deletion bound whose own message says to run with this flag to see
    /// the list it was refusing to show.
    /// </summary>
    [Fact]
    public async Task ADryRun_ShowsWhatAdoptingWouldDo_RatherThanRefusingToSay()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(copy);
            await File.WriteAllTextAsync(Path.Combine(copy, "stray.txt"), "x\n", cancellationToken);

            var result = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(DryRun: true), cancellationToken);

            Assert.Contains("stray.txt", result.Plan.Deletes);
            Assert.Contains(result.Plan.Describe(SyncVerb.Planned), line => line.Contains("src/a.c", StringComparison.Ordinal));

            // Nothing was changed, and the reader is told which flag would do it.
            Assert.True(File.Exists(Path.Combine(copy, "stray.txt")));
            Assert.Contains("--adopt", harness.StandardOutput.ToString(), StringComparison.Ordinal);
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
        var harness = new HarnessFactory();

        var refusal = Assert.Throws<HarnessException>(
            () => ((LocalSyncTransport)Transport(harness)).Resolve("/host/repo", path));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
    }

    [Fact]
    public async Task TheAgentAnswersAManifestRequest_WithTheArgumentsARemoteSyncSends()
    {
        // The far side of every remote sync. Every non-created path reads the copy's manifest, and
        // the verification afterwards reads it again, so an agent that refuses this request is an
        // agent no host can be synced to — and nothing else in the suite goes through it.
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        temp.WriteFile("src/app.cs", "// code");

        var result = await CliRunner.RunAsync(
            ["sync-serve", SyncServe.Manifest, temp.Path, ".git\n.harness-config"],
            token);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        var answer = SyncServe.ReadAnswer<SyncManifestAnswer>(result.StandardOutput.Trim());

        Assert.NotNull(answer);
        Assert.Contains(answer.Entries, entry => entry.Path == "src/app.cs");
    }

    private static LocalSyncTransport Transport(HarnessFactory harness)
        => new LocalSyncTransport(
            harness.FileSystem,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            harness.GitClient,
            harness.Platform);

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
