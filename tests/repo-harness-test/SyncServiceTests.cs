using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Repository;
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
    /// Taking it over is one flag. What survives is what <c>sync.neverTransfer</c> names — <c>build</c>
    /// by default — and not, as this test once claimed, whatever git ignores on that host: the ignore
    /// list is read from this tree by listing the ignored files that exist <em>here</em>, so a
    /// directory only the host has is ignored by nothing this side can see.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheHarnessDidNotCreate_IsTakenOverBy_Adopt_KeepingWhatNeverTransferNames()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // A checkout of the same repository, as somebody would really have made it: the same
            // files, one of them stale, one stray file, and a warm build tree.
            Directory.CreateDirectory(Path.Combine(copy, "src"));
            Directory.CreateDirectory(Path.Combine(copy, "build"));
            await File.WriteAllTextAsync(Path.Combine(copy, ".gitignore"), "build/\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "a.c"), "stale\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "b.c"), "b\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "stray.txt"), "x\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "build", "warm.o"), "object\n", cancellationToken);

            var result = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["local"]), cancellationToken);

            Assert.True(result.Verified);

            Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(copy, "src", "a.c"), cancellationToken), StringComparer.Ordinal);
            Assert.False(File.Exists(Path.Combine(copy, "stray.txt")));

            // sync.neverTransfer names build, so the hours of output in it are still there.
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
    /// One sync reaches every host, so saying yes once must not say yes everywhere. A host nobody
    /// named is refused exactly as it would have been without the flag.
    /// </summary>
    [Theory]
    [InlineData("local", true)]
    [InlineData("LOCAL", true)]
    [InlineData("vps", false)]
    [InlineData("ssh vps", false)]
    public void Adopt_NamesTheHostsItTakesOver_AndNoOthers(string named, bool adopted)
        => Assert.Equal(adopted, new SyncOptions(Adopt: [named]).Adopts(HostId.Local));

    [Fact]
    public void Adopt_NamingNobody_TakesOverNothing()
        => Assert.False(new SyncOptions().Adopts(HostId.Local));

    /// <summary>
    /// A host that was not named is still refused, and the refusal names the flag that would take
    /// that host in particular rather than a flag that would take every one of them.
    /// </summary>
    [Fact]
    public async Task ADirectoryOnAHostNobodyNamed_IsStillRefused_EvenWhileAnotherIsBeingAdopted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(copy);
            await File.WriteAllTextAsync(Path.Combine(copy, "theirs.txt"), "x\n", cancellationToken);

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["some-other-host"]), cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("--adopt local", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(copy, "theirs.txt")));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// Afterwards a taken-over directory and one the tool made itself are the same directory, and
    /// only one of them deleted somebody's files. The marker is the only place that can still say so.
    /// </summary>
    [Fact]
    public async Task TheMarker_RecordsWhetherTheCopyWasTakenOver_OrMade()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var made = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var taken = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            _ = await service.SyncAsync(temp.Path, Transport(harness), made, new SyncOptions(), cancellationToken);

            // A checkout of the same repository with one stray file, so the deletion bound — which
            // this test is not about — has nothing to say.
            Directory.CreateDirectory(Path.Combine(taken, "src"));
            await File.WriteAllTextAsync(Path.Combine(taken, ".gitignore"), "build/\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(taken, "src", "a.c"), "a\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(taken, "src", "b.c"), "b\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(taken, "theirs.txt"), "x\n", cancellationToken);

            _ = await service.SyncAsync(
                temp.Path, Transport(harness), taken, new SyncOptions(Adopt: ["local"]), cancellationToken);

            var madeMarker = await File.ReadAllTextAsync(Marker(made), cancellationToken);
            var takenMarker = await File.ReadAllTextAsync(Marker(taken), cancellationToken);

            Assert.Contains("\"Adopted\": false", madeMarker, StringComparison.Ordinal);
            Assert.Contains("\"Adopted\": true", takenMarker, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(made);
            DeleteIfPresent(taken);
        }
    }

    private static string Marker(string root)
        => Path.Combine(root, HarnessLayout.DirectoryName, "synced-copy.json");

    /// <summary>
    /// The correction to what this feature once promised. Ignored paths are listed from the source by
    /// asking git which ignored files exist <em>there</em>, so a directory only the host has is
    /// protected by nothing and is deleted like any other file. It has to appear in the list a reader
    /// decides on, and the remedy — naming it in <c>sync.neverTransfer</c> — has to be in the refusal.
    /// </summary>
    [Fact]
    public async Task ADirectoryOnlyTheCopyHas_IsDeletedByAnAdoption_EvenWhenItsNameIsIgnoredHere()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // 'packages/' is ignored by the source's .gitignore, and the source has no such directory,
            // so git lists nothing for it and nothing here knows to withhold it.
            await File.AppendAllTextAsync(Path.Combine(temp.Path, ".gitignore"), "packages/\n", cancellationToken);
            await harness.CommitAllAsync(temp.Path, "ignore packages", cancellationToken);

            Directory.CreateDirectory(Path.Combine(copy, "packages"));
            await File.WriteAllTextAsync(Path.Combine(copy, "packages", "warm.bin"), "hours\n", cancellationToken);

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            // Named as a deletion rather than quietly promised as safe, and the remedy named with it.
            Assert.Contains("delete    packages/warm.bin", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("sync.neverTransfer", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A directory that exists and carries no marker is what a mistyped <c>repositoryPath</c> produces,
    /// which is the case the deletion bound was written for. Adopting must not be a way around it.
    /// </summary>
    [Fact]
    public async Task AnAdoptionThatWouldEmptyTheDirectory_IsStillRefusedByTheDeletionBound()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // What a home directory looks like when repositoryPath was meant to name a checkout
            // underneath it: nothing of the source, everything of somebody's.
            Directory.CreateDirectory(Path.Combine(copy, "other-project"));

            for (var file = 0; file < 8; file++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(copy, "other-project", $"work-{file}.txt"),
                    "theirs\n",
                    cancellationToken);
            }

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["local"]), cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("maxDeleteFraction", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(copy, "other-project", "work-0.txt")));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// Somebody who reads <c>--adopt</c> in the help and types it straight away never sees a refusal,
    /// and everything naming what taking a directory over costs used to live inside one.
    /// </summary>
    [Fact]
    public async Task Adopting_SaysWhatItIsAboutToCost_EvenWhenNoRefusalEverRan()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(Path.Combine(copy, "src"));
            await File.WriteAllTextAsync(Path.Combine(copy, ".gitignore"), "build/\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "a.c"), "months of uncommitted work\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "b.c"), "b\n", cancellationToken);

            _ = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["local"]), cancellationToken);

            var said = harness.StandardError.ToString();

            Assert.Contains("adopting", said, StringComparison.Ordinal);
            Assert.Contains("overwrite src/a.c", said, StringComparison.Ordinal);
            Assert.Contains("config.json", said, StringComparison.Ordinal);
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
