using System.Globalization;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
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

    /// <summary>
    /// A copy's git index holds every file the sync carried and the configuration it placed, and nothing
    /// else: a file the tree drops leaves it on the next sync. Written without staging one, a copy's index
    /// named nothing, so every build there fingerprinted no input and each after the first started from
    /// clean, while every guard watching the inputs watched nothing.
    /// </summary>
    [Fact]
    public async Task ACopysIndex_HoldsExactlyWhatTheSyncCarried()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        async Task<IReadOnlyList<string>> IndexedAsync()
            => [.. (await harness.GitClient.ListIndexAsync(copy, cancellationToken)).Select(entry => entry.Path).Order(StringComparer.Ordinal)];

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            var indexed = await IndexedAsync();
            Assert.Contains("src/a.c", indexed);
            Assert.Contains("src/b.c", indexed);
            Assert.Contains(".gitignore", indexed);
            Assert.Contains(".harness-config/config.json", indexed);

            // Nothing the copy holds that the sync did not carry: the marker is the harness's own.
            Assert.DoesNotContain($".harness-config/{HarnessLayout.SyncedCopyMarkerName}", indexed);

            File.Delete(Path.Combine(temp.Path, "src", "a.c"));
            await harness.CommitAllAsync(temp.Path, "drop a", cancellationToken);

            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.DoesNotContain("src/a.c", await IndexedAsync());
            Assert.Contains("src/b.c", await IndexedAsync());
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A copy made before its index was kept - one whose index names nothing - is put right by the next
    /// sync, even one that carries nothing: the consumer's copies are all such copies, and waiting for a
    /// file to change before a build can keep its directory would leave them rebuilding from clean.
    /// </summary>
    [Fact]
    public async Task ACopyWhoseIndexNamesNothing_IsPutRightByASyncThatCarriesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            // As every copy made before this was: its files there, and its index naming none of them.
            var emptied = await harness.GitClient.RunAsync(copy, ["read-tree", "--empty"], cancellationToken: cancellationToken);
            Assert.True(emptied.Succeeded, emptied.FailureMessage);
            Assert.Empty(await harness.GitClient.ListIndexAsync(copy, cancellationToken));

            var again = await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.Empty(again.Plan.Writes);
            Assert.Contains(
                "src/a.c",
                (await harness.GitClient.ListIndexAsync(copy, cancellationToken)).Select(entry => entry.Path));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A copy placed inside another repository's work tree is made a repository of its own, and the other
    /// one's index is never written: git found that repository from the copy, so staging what the sync
    /// carried put every file into it, and took out whatever it tracked below the copy.
    /// </summary>
    [Fact]
    public async Task ACopyInsideAnotherRepository_IsARepositoryOfItsOwn_AndTheOtherIndexIsLeftAlone()
    {
        using var temp = new TempDirectory();
        using var outer = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);

        var created = await harness.GitClient.RunAsync(outer.Path, ["init", "--quiet", "."], cancellationToken: cancellationToken);
        Assert.True(created.Succeeded, created.FailureMessage);

        var copy = outer.Combine("hosts", "copy");

        await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

        Assert.Empty(await harness.GitClient.ListIndexAsync(outer.Path, cancellationToken));
        Assert.Equal(string.Empty, (await harness.GitClient.GetLocationAsync(copy, cancellationToken))?.Prefix);
        Assert.Contains(
            "src/a.c",
            (await harness.GitClient.ListIndexAsync(copy, cancellationToken)).Select(entry => entry.Path));
    }

    /// <summary>
    /// A file the sync writes where the copy held a link is a file of the copy like any other - the write
    /// replaced the link - and its index holds it. Taken for one beyond a link, it was left out, and every
    /// build and guard there passed over it until the next sync.
    /// </summary>
    [Fact]
    public async Task AFileWrittenWhereTheCopyHeldALink_IsInItsIndex()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            var replaced = Path.Combine(copy, "src", "a.c");
            File.Delete(replaced);

            try
            {
                File.CreateSymbolicLink(replaced, "b.c");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip($"This machine cannot make a link: {ex.Message}");
            }

            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.Null(new FileInfo(replaced).LinkTarget);
            Assert.Contains(
                "src/a.c",
                (await harness.GitClient.ListIndexAsync(copy, cancellationToken)).Select(entry => entry.Path));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A sync carries its writes in batches, so the far side is asked once for many files rather than once
    /// for each. Over a connection one asking is one session, and a session costs a connection, an
    /// authentication and whatever the host's login profile does: a file at a time, a consumer's first sync
    /// of a worktree's copy opened 2,446 sessions, ran 1,136 seconds, and outlasted the host's wake.
    /// </summary>
    [Fact]
    public async Task ASyncCarriesItsWritesInBatches_AskingTheFarSideFarFewerTimesThanItHasFiles()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        // More files than one batch may hold, so the grouping itself is exercised rather than assumed.
        var extra = SyncServe.MostFilesInABatch + 20;

        for (var index = 0; index < extra; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(temp.Path, "src", $"f{index.ToString("D4", CultureInfo.InvariantCulture)}.c"),
                $"file {index}\n",
                cancellationToken);
        }

        await harness.CommitAllAsync(temp.Path, "many files", cancellationToken);

        try
        {
            var recording = new RecordingTransport(Transport(harness));

            var result = await service.SyncAsync(temp.Path, recording, copy, new SyncOptions(), cancellationToken);

            Assert.True(result.Verified);
            Assert.True(recording.Written.Count > extra, $"only {recording.Written.Count} files were written");

            // Nothing of the plan crosses on its own: a file at a time is the cost this exists to avoid, and
            // a batch that merely wrapped single writes would satisfy any bound on how many batches there
            // are. The one write outside a batch is the configuration, placed on its own after the transfer
            // so that a copy which failed part way never looks like one a leg could run in.
            Assert.Equal(1, recording.SingleWrites);

            // And the batches are as few as the bounds allow, not merely fewer than the files: sessions grow
            // with the tree's size divided by a batch, which is what makes a first sync finish.
            Assert.InRange(recording.Batches, 1, (recording.Written.Count / SyncServe.MostFilesInABatch) + 2);

            // No file is lost to the grouping, and the count bound is the one that fired here.
            Assert.True(File.Exists(Path.Combine(copy, "src", "f0000.c")));
            Assert.True(File.Exists(Path.Combine(copy, "src", $"f{(extra - 1).ToString("D4", CultureInfo.InvariantCulture)}.c")));
            Assert.Equal(recording.Written.Count, recording.Written.Distinct(StringComparer.Ordinal).Count());
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
    /// hosts.wsl and hosts.ssh are separate maps and nothing stops the same key in both, so a bare
    /// name can answer to two machines. Taking over the one somebody did not mean deletes what it
    /// holds, and that is the blanket permission naming hosts exists to end — let back in through
    /// the convenience of a short name.
    /// </summary>
    [Fact]
    public void Adopt_RefusesABareNameTwoHostsAnswerTo_AndNamesBothSpellings()
    {
        var declared = new[] { HostId.Wsl("dev"), HostId.Ssh("dev") };

        var refusal = Assert.Throws<HarnessException>(
            () => new SyncOptions(Adopt: ["dev"]).RefuseWhenNamingNoOneHost(declared));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("wsl dev", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ssh dev", refusal.Message, StringComparison.Ordinal);

        // Spelled in full it is never ambiguous, and takes only the one it names.
        var whole = new SyncOptions(Adopt: ["wsl dev"]);
        whole.RefuseWhenNamingNoOneHost(declared);

        Assert.True(whole.Adopts(HostId.Wsl("dev")));
        Assert.False(whole.Adopts(HostId.Ssh("dev")));
    }

    /// <summary>
    /// A name no host answers to is a typo, and a typo nobody is told about is learned when the host
    /// somebody meant to take over is refused instead — or never, if it was already a copy.
    /// </summary>
    [Fact]
    public void Adopt_RefusesANameNoHostAnswersTo_AndSaysWhichHostsThereAre()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => new SyncOptions(Adopt: ["vsp"]).RefuseWhenNamingNoOneHost([HostId.Ssh("vps")]));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("'vsp'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ssh vps", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that was not named is still refused, and the refusal names the flag that would take
    /// that host in particular rather than a flag that would take every one of them.
    /// </summary>
    [Fact]
    public async Task ADirectoryOnAHostNobodyNamed_IsStillRefused_AndTheRefusalNamesThatHost()
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
            Assert.Contains("--adopt \"local\"", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(copy, "theirs.txt")));

            // Nothing was ever started on this directory, so there is nothing to finish. Told
            // otherwise, somebody goes looking for the run that began deleting it.
            Assert.Contains("Taking it over is", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Finishing it", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A takeover that stopped part way is neither the checkout somebody had nor a copy of this
    /// tree. Marked complete it would be taken for this tool's own, and the next ordinary build or
    /// test — which never carries an adopt list — would delete the rest with nobody asked. So it
    /// still needs somebody to say go ahead, and the refusal says the earlier run already removed
    /// things the list below cannot show.
    /// </summary>
    [Fact]
    public async Task AnAdoptionThatStoppedPartWay_StillNeedsSayingSoAgain()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // Exactly what a run leaves behind when it marks the takeover and then dies part way:
            // some of this tree already written, some of theirs still there.
            Directory.CreateDirectory(Path.Combine(copy, HarnessLayout.DirectoryName));
            Directory.CreateDirectory(Path.Combine(copy, "src"));
            await File.WriteAllTextAsync(Path.Combine(copy, ".gitignore"), "build/\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "a.c"), "a\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "b.c"), "b\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "theirs.txt"), "x\n", cancellationToken);
            await File.WriteAllTextAsync(
                Marker(copy),
                "{ \"CreatedUtc\": \"2026-01-01T00:00:00.0000000+00:00\", \"CreatedBy\": \"somewhere\", "
                + "\"Adopted\": true, \"Completed\": false }",
                cancellationToken);

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("stopped before it finished", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("is not in the list below", refusal.Message, StringComparison.Ordinal);

            // Only something that was started can be finished, and only this state was.
            Assert.Contains("Finishing it is", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(copy, "theirs.txt")));

            // Said again, it finishes, and is a copy from then on.
            var finished = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["local"]), cancellationToken);

            Assert.True(finished.Verified);
            Assert.Contains("\"Completed\": true", await File.ReadAllTextAsync(Marker(copy), cancellationToken), StringComparison.Ordinal);
            Assert.Contains("\"Adopted\": true", await File.ReadAllTextAsync(Marker(copy), cancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A link sits in no manifest — the walk refuses to follow one — so a file written at its name
    /// replaces it and reads as an ordinary write, and everything behind a linked directory is
    /// outside every list a plan can build. Somebody deciding whether to hand this tool a directory
    /// has no other way to see them.
    /// </summary>
    [Fact]
    public async Task ALinkInTheCopy_IsNamedInWhatAdoptingWouldCost_BecauseNoPlanCanSpeakForIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var elsewhere = Path.Combine(temp.Path, "..", "elsewhere-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(copy);
            Directory.CreateDirectory(elsewhere);
            await File.WriteAllTextAsync(Path.Combine(elsewhere, "theirs.txt"), "x\n", cancellationToken);

            if (!TryLink(Path.Combine(copy, "data"), elsewhere))
            {
                Assert.Skip("Creating a link needs a privilege this machine did not grant.");
            }

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Contains("through   data", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("a link", refusal.Message, StringComparison.Ordinal);

            // And what it points at is untouched, because a refusal changed nothing.
            Assert.True(File.Exists(Path.Combine(elsewhere, "theirs.txt")));
        }
        finally
        {
            DeleteIfPresent(copy);
            DeleteIfPresent(elsewhere);
        }
    }

    /// <summary>
    /// The bound fires before anything is taken over, so its remedy has to be the one that applies:
    /// check the directory, not the source tree the reader never doubted.
    /// </summary>
    [Fact]
    public async Task TheDeletionBound_TellsAnAdopterToCheckTheDirectory_NotTheSource()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
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

            Assert.Contains("directory you meant to take over", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("repositoryPath", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("source tree is the one", refusal.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// A path --pull names that the copy does not hold fails the pull by name, as a write that fails does:
    /// nothing at it, nothing along it, or a directory. Read by the runtime alone, the first two ended the
    /// host's command as an unexpected DirectoryNotFoundException or FileNotFoundException - a defect in
    /// the tool, exit 70 - with the path only inside the runtime's own words. Never a refusal, which would
    /// end a whole run where the same read found a file a sync's plan listed removed since.
    /// </summary>
    [Theory]
    [InlineData("out/missing.txt", "nothing is at that path there")]
    [InlineData("absent/deeper/report.txt", "nothing is at that path there")]
    [InlineData("out", "is a directory in")]
    public async Task APathThePullNamesThatTheCopyDoesNotHold_IsRefusedByName(string path, string reason)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var landing = Path.Combine(temp.Path, "artefacts");

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);
            Directory.CreateDirectory(Path.Combine(copy, "out"));

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.PullAsync(
                Transport(harness), copy, landing, [path], cancellationToken));

            Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
            Assert.False(HarnessExit.RefusesTheRun(refusal.ExitCode));
            Assert.StartsWith($"'{path}' ", refusal.Message, StringComparison.Ordinal);
            Assert.Contains(reason, refusal.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(landing), "Something landed for a path that was not there.");
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

    /// <summary>
    /// The copy holds a directory where this tree holds a file. The two shapes of that collision
    /// arrive as different exception types — a file where a directory is wanted fails the
    /// directory's creation, a directory where a file is wanted fails the replace — so catching
    /// only one leaves the other escaping as a defect in this tool, for an ordinary condition in
    /// somebody's own directory.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APathTheCopyHoldsAsTheOtherKindOfThing_IsNamedWithItsRemedy(bool directoryWhereAFileGoes)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // A copy that resembles the source, so the deletion bound is not what stops this: it
            // measures deletions against what the directory holds, and a directory holding nothing
            // the source has is nothing but deletion.
            await MirrorAsync(temp.Path, copy, cancellationToken);

            if (directoryWhereAFileGoes)
            {
                // src/a.c is a file in the source tree and a directory here.
                File.Delete(Path.Combine(copy, "src", "a.c"));
                Directory.CreateDirectory(Path.Combine(copy, "src", "a.c"));
                await File.WriteAllTextAsync(Path.Combine(copy, "src", "a.c", "inside.txt"), "x\n", cancellationToken);
            }
            else
            {
                // src is a directory in the source tree and a file here.
                Directory.Delete(Path.Combine(copy, "src"), recursive: true);
                await File.WriteAllTextAsync(Path.Combine(copy, "src"), "not a directory\n", cancellationToken);
            }

            var failure = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["local"]), cancellationToken));

            Assert.Contains("the other kind of thing", failure.Message, StringComparison.Ordinal);
            Assert.Contains("remove it there", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A marker this build cannot read must never come back as a copy this tool made and finished.
    /// That answer is the most permissive one there is — it is what lets an ordinary build or test,
    /// which never carries an adopt list, delete a directory without asking — and a damaged file
    /// says nothing about who wrote it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{ not json at all")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"CreatedUtc\": \"2026-01-01T00:00:00.0000000+00:00\", \"CreatedBy\": \"x\", \"Whatever\": 1 }")]
    public async Task AMarkerThisBuildCannotRead_IsRefused_NotTakenForACopyItMade(string content)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(Path.Combine(copy, HarnessLayout.DirectoryName));
            await File.WriteAllTextAsync(Path.Combine(copy, "theirs.txt"), "x\n", cancellationToken);
            await File.WriteAllTextAsync(Marker(copy), content, cancellationToken);

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("cannot read it", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(copy, "theirs.txt")));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// The highest-consequence path in the whole marker change, and the one with no visible symptom
    /// until it is wrong: a marker written before takeovers existed carries neither flag, and has
    /// to keep meaning a finished copy. Read as anything else, every copy on every host demands
    /// '--adopt' on the next sync.
    /// </summary>
    [Fact]
    public async Task AMarkerFromBeforeTakeoversExisted_IsStillACopyThisToolMade()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(Path.Combine(copy, HarnessLayout.DirectoryName));
            await File.WriteAllTextAsync(
                Marker(copy),
                "{ \"CreatedUtc\": \"2026-01-01T00:00:00.0000000+00:00\", \"CreatedBy\": \"somewhere\" }",
                cancellationToken);

            // No adopt list, exactly as build, test and run always sync.
            var result = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.True(result.Verified);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A marker that is there and cannot be opened is the same question as one that cannot be
    /// parsed, and has to be refused in the same words rather than escaping as a file error with no
    /// exit code of its own. On Windows an indexer, a scanner or a second sync makes this ordinary.
    /// </summary>
    [Fact]
    public async Task AMarkerNothingCanOpen_IsRefusedLikeOneNothingCanParse()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only here does holding a file open stop another reader.");

        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            using (new FileStream(Marker(copy), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                    temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

                Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
                Assert.Contains("cannot read it", refusal.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// Links are named however much else the copy has to lose. Appended after the bound that cuts a
    /// long list, they would be the first thing dropped — in exactly the directory that has the most
    /// hidden behind them.
    /// </summary>
    [Fact]
    public async Task ALinkIsNamed_EvenWhenTheRestOfTheLossIsTooLongToPrint()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var elsewhere = Path.Combine(temp.Path, "..", "elsewhere-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(elsewhere);
            await MirrorAsync(temp.Path, copy, cancellationToken);

            for (var file = 0; file < 25; file++)
            {
                await File.WriteAllTextAsync(Path.Combine(copy, $"theirs-{file}.txt"), "x\n", cancellationToken);
            }

            if (!TryLink(Path.Combine(copy, "data"), elsewhere))
            {
                Assert.Skip("Creating a link needs a privilege this machine did not grant.");
            }

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Contains("...and", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("through   data", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
            DeleteIfPresent(elsewhere);
        }
    }

    /// <summary>
    /// A link to a file is recorded by the other half of the walk, and is the case the refusal
    /// speaks about most directly: a file written at its name replaces the link and reads as an
    /// ordinary write.
    /// </summary>
    [Fact]
    public async Task ALinkToAFile_IsNamedToo_NotOnlyALinkedDirectory()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var elsewhere = Path.Combine(temp.Path, "..", "elsewhere-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(Path.Combine(copy, "src"));
            Directory.CreateDirectory(elsewhere);
            await File.WriteAllTextAsync(Path.Combine(elsewhere, "real.txt"), "x\n", cancellationToken);

            if (!TryLink(Path.Combine(copy, "src", "linked.txt"), Path.Combine(elsewhere, "real.txt"), file: true))
            {
                Assert.Skip("Creating a link needs a privilege this machine did not grant.");
            }

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Contains("through   src/linked.txt", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(elsewhere, "real.txt")));
        }
        finally
        {
            DeleteIfPresent(copy);
            DeleteIfPresent(elsewhere);
        }
    }

    /// <summary>
    /// The agent computes the links and has to send them: every host a sync reaches is a far side,
    /// so a manifest answer that drops them leaves the list an adoption prints silent about exactly
    /// the paths no plan can speak for.
    /// </summary>
    [Fact]
    public async Task TheAgentSendsTheLinksItRefusedToFollow_BecauseTheAskingMachineCannotSeeThem()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        temp.WriteFile("src/app.cs", "// code");

        if (!TryLink(Path.Combine(temp.Path, "data"), Path.Combine(temp.Path, "src")))
        {
            Assert.Skip("Creating a link needs a privilege this machine did not grant.");
        }

        var result = await CliRunner.RunAsync(
            ["sync-serve", SyncServe.Manifest, temp.Path, ".git\n.harness-config"],
            token);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        var answer = SyncServe.ReadAnswer<SyncManifestAnswer>(result.StandardOutput.Trim());

        Assert.NotNull(answer);
        Assert.Contains("data", answer.Links, StringComparer.Ordinal);
    }

    /// <summary>
    /// A sync that could reach none of the hosts its legs need copied nothing it was asked to, and fails
    /// naming them - a real run and a dry run alike, since a dry run answers for the real one. Counting
    /// the reachable hosts alone read this as no host needing a copy: inside a host's own copy, which
    /// reaches no other machine, every leg warned it could not run and the sync still concluded OK.
    /// Inside a copy the conclusion also says where the command belongs, once, whatever the host count.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ASyncThatCanReachNoHostItsLegsNeed_FailsNamingThem_RatherThanConcludingOk(bool syncedCopy, bool dryRun)
    {
        var factory = Substitute.For<ISyncTransportFactory>();
        var harness = new HarnessFactory();
        var here = TestHost.TemporaryRoot;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" },
                    ["mac"] = new SshHostConfig { RepositoryPath = "/Users/harness/repo" },
                },
            },
            Legs = { ["arm"] = HostDoubles.Leg("linux", "arm64"), ["mac"] = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug", Ssh = "mac" } },
        };

        var loader = HostDoubles.Loader(config, here, syncedCopy: syncedCopy);

        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "linux", Processor = "x86_64" }
            : new HostReport { Host = host, Reason = "the host could not be reached" });

        var service = new SyncService(
            loader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Transport(harness),
            factory,
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var outcome = await service.SyncHostsAsync(
            here, null, new SyncOptions(DryRun: dryRun), [], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, outcome.ExitCode);
        Assert.StartsWith("no host a leg is placed on could be reached, so nothing was copied", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("ssh pi", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("ssh mac", outcome.Message, StringComparison.Ordinal);

        // Said once as the command ends, never in its conclusion, where it once repeated each host's refusal.
        Assert.DoesNotContain(HostConnector.SyncedCopyNotice, outcome.Message, StringComparison.Ordinal);

        factory.DidNotReceive().For(Arg.Any<HostReport>());
    }

    /// <summary>
    /// A sync whose every leg runs on this machine needed no copy anywhere, and says so as the success it
    /// is - the conclusion the failure above must not be mistaken for, and must not replace.
    /// </summary>
    [Fact]
    public async Task ASyncWhoseLegsAllRunHere_SaysNoHostNeedsACopy()
    {
        var factory = Substitute.For<ISyncTransportFactory>();
        var harness = new HarnessFactory();
        var here = TestHost.TemporaryRoot;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } } },
            Legs = { ["here"] = HostDoubles.Leg("linux", "x86_64") },
        };

        var loader = HostDoubles.Loader(config, here, syncedCopy: true);

        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "linux", Processor = "x86_64" }
            : throw new InvalidOperationException($"{host} was measured, though no leg needs it"));

        var service = new SyncService(
            loader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Transport(harness),
            factory,
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var outcome = await service.SyncHostsAsync(here, null, new SyncOptions(), [], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("no host needs a copy: every runnable leg runs on this machine", outcome.Message);
    }

    /// <summary>
    /// A leg named with --legs that no host could take fails the sync, as it fails 'legs', though another
    /// host was reached and done: the leg was asked for, and its copy was not made. Such a sync said how
    /// many hosts it had done, and exited 0. A leg merely declared on a machine that is off is normal, and
    /// fails nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALegNamedWithLegsThatNoHostCouldTake_FailsTheSync_ThoughAnotherHostWasReached(bool named)
    {
        var harness = new HarnessFactory();
        var here = TestHost.TemporaryRoot;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" },
                    ["mac"] = new SshHostConfig { RepositoryPath = "/Users/harness/repo" },
                },
            },
            Legs = { ["arm"] = HostDoubles.Leg("linux", "arm64"), ["mac"] = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug", Ssh = "mac" } },
        };

        var loader = HostDoubles.Loader(config, here);
        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "linux", Processor = "x86_64" }
            : host == HostId.Ssh("pi")
                ? new HostReport { Host = host, Reason = "the host could not be reached" }
                : new HostReport { Host = host, Os = "macos", Processor = "arm64" });

        var service = new SyncService(
            loader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Transport(harness),
            Substitute.For<ISyncTransportFactory>(),
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var outcome = await service.SyncHostsAsync(
            here, named ? ["arm", "mac"] : null, new SyncOptions(DryRun: true), ["build/report.txt"], TestContext.Current.CancellationToken);

        // The host that was reached is done either way.
        Assert.Equal(["ssh mac: would bring back 1 named file(s) from '/Users/harness/repo'"], outcome.Details);

        if (named)
        {
            Assert.Equal(LegsExit.Unavailable, outcome.ExitCode);
            Assert.Contains("1 leg(s) could not be placed on any host, so no copy was made for them: arm", outcome.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        }
    }

    /// <summary>
    /// A sync where no selected leg could be placed on any host, and no host was out of reach - no host
    /// runs the system its legs name - fails as 'legs' fails. It said that no host needed a copy, and that
    /// every runnable leg ran on this machine, when no leg could run anywhere.
    /// </summary>
    [Fact]
    public async Task ASyncWhereNoLegCouldBePlaced_Fails_RatherThanSayingNoHostNeedsACopy()
    {
        var harness = new HarnessFactory();
        var here = TestHost.TemporaryRoot;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["mac"] = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug" } },
        };

        var loader = HostDoubles.Loader(config, here);
        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "linux", Processor = "x86_64" }
            : throw new InvalidOperationException($"{host} was measured, though no host is declared"));

        var service = new SyncService(
            loader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Transport(harness),
            Substitute.For<ISyncTransportFactory>(),
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var outcome = await service.SyncHostsAsync(here, null, new SyncOptions(), [], TestContext.Current.CancellationToken);

        Assert.Equal(LegsExit.Unavailable, outcome.ExitCode);
        Assert.Equal("no host was reached; 1 leg(s) could not be placed on any host, so no copy was made for them: mac", outcome.Message);
    }

    /// <summary>
    /// A name no host answers to is refused before a transport is made for any host, so a typo
    /// cannot be learned about after another host's directory has already been taken over.
    /// </summary>
    [Fact]
    public async Task AnAdoptNameNothingAnswersTo_IsRefusedBeforeAnyHostIsReached()
    {
        var factory = Substitute.For<ISyncTransportFactory>();
        var harness = new HarnessFactory();
        var here = TestHost.TemporaryRoot;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } } },
            Legs = { ["arm"] = HostDoubles.Leg("linux", "arm64") },
        };

        var loader = HostDoubles.Loader(config, here);

        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "linux", Processor = "x86_64" }
            : new HostReport { Host = host, Os = "linux", Processor = "arm64" });

        var service = new SyncService(
            loader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Transport(harness),
            factory,
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncHostsAsync(
            here, null, new SyncOptions(Adopt: ["p1"]), [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("ssh pi", refusal.Message, StringComparison.Ordinal);

        // The ordering is the whole point: no transport was made, so nothing could have been
        // deleted anywhere before the typo was found.
        factory.DidNotReceive().For(Arg.Any<HostReport>());
    }

    /// <summary>
    /// A copy starts no program on a host, so a host is given one whatever it lacks: a host without a
    /// leg's test runner is still where a runner that runs something else goes, and where its
    /// artifacts are carried. Placed as a test would place it, the host was never reached.
    /// </summary>
    [Fact]
    public async Task AHostLackingALegsPrograms_IsStillSynced()
    {
        var transports = Substitute.For<ISyncTransportFactory>();
        transports.For(Arg.Any<HostReport>()).Returns(_ => throw new InvalidOperationException("reached"));

        var harness = new HarnessFactory();
        var here = TestHost.TemporaryRoot;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } } },
            Legs =
            {
                ["arm"] = new LegConfig
                {
                    Os = "linux",
                    Processor = "arm64",
                    Config = "debug",
                    Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "passed" } },
                },
            },
        };

        var loader = HostDoubles.Loader(config, here);

        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "linux", Processor = "x86_64" }
            : new HostReport
            {
                Host = host,
                Os = "linux",
                Processor = "arm64",
                Programs = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
                {
                    ["ctest"] = new("ctest", ProgramFound.Nowhere),
                },
            });

        var service = new SyncService(
            loader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Transport(harness),
            transports,
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var reached = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncHostsAsync(
            here, null, new SyncOptions(), [], TestContext.Current.CancellationToken));

        Assert.Equal("reached", reached.Message);
        transports.Received(1).For(Arg.Is<HostReport>(report => report.Host.Equals(HostId.Ssh("pi"))));
    }

    /// <summary>
    /// A host's name is two words and --adopt takes several names, so the spelling a refusal tells
    /// somebody to type has to survive their shell and this tool's own parser. Unquoted it arrives
    /// as two names, and following the instruction lands on a usage error.
    /// </summary>
    [Fact]
    public void TheSpellingARefusalSuggests_IsOneNameAfterTheShellHasReadIt()
    {
        var declared = new[] { HostId.Ssh("vps") };

        // As the refusal prints it: --adopt "ssh vps". A shell hands that over as one argument.
        var quoted = new SyncOptions(Adopt: ["ssh vps"]);
        quoted.RefuseWhenNamingNoOneHost(declared);

        Assert.True(quoted.Adopts(HostId.Ssh("vps")));

        // Without the quotes the parser splits it, and the first half names nothing.
        var refusal = Assert.Throws<HarnessException>(
            () => new SyncOptions(Adopt: ["ssh", "vps"]).RefuseWhenNamingNoOneHost(declared));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("quote it", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host ssh never connected to is said as that, in ssh's words, whatever the sync asked it: nothing
    /// ran there, where a command that never said how it finished may have run in part.
    /// </summary>
    [Fact]
    public async Task AHostSshNeverConnectedTo_IsSaidAsThat_WhateverTheSyncAskedIt()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var commands = new ScriptedHostCommands((_, _) => HostResults.Failed(255, "banner exchange: Connection to UNKNOWN port -1: Connection refused\r\n"));

        var transport = new RemoteSyncTransport(
            HostId.Ssh("vps"),
            new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
            commands,
            new ConsoleHarnessOutput(output, error, verbose: false));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => transport.ReadManifestAsync(
            "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Equal("ssh vps: the host could not be reached: ssh said banner exchange: Connection to UNKNOWN port -1: Connection refused", refusal.Message);
    }

    /// <summary>
    /// A manifest answer that never arrived is refused, not read as a copy holding nothing. Empty is
    /// the most dangerous answer available here: the deletion bound measures against what the copy
    /// holds, so an empty one disables it, and the refusal then tells somebody that taking the
    /// directory over would remove nothing — advice they act on, after which the read succeeds and
    /// everything the host held goes.
    /// </summary>
    [Fact]
    public async Task AManifestTheHostNeverAnswered_IsRefused_NotReadAsAnEmptyCopy()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        // The agent runs, says it finished cleanly, and its answer line never arrives: a line
        // mangled on the way, or an earlier one that ended the read.
        var commands = new ScriptedHostCommands((_, command) => HostResults.Finished(command, HarnessExit.Success));

        var transport = new RemoteSyncTransport(
            HostId.Ssh("vps"),
            new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
            commands,
            new ConsoleHarnessOutput(output, error, verbose: false));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => transport.ReadManifestAsync(
            "/home/dev/repo", [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Contains("did not answer with what", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A takeover that could not be verified is left as one that stopped part way, so the next run
    /// asks again rather than treating a directory whose content nobody could confirm as this tool's
    /// own — which is the state from which an ordinary build or test deletes without asking.
    /// </summary>
    [Fact]
    public async Task ATakeoverThatFailedItsVerification_IsNotMarkedFinished()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await MirrorAsync(temp.Path, copy, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "theirs.txt"), "x\n", cancellationToken);

            // Everything the real transport does, except that the last manifest read — the one the
            // verification compares against — comes back missing a file.
            var transport = new RecordingTransport(Transport(harness), losesAFileWhenVerifying: true);

            var failure = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, transport, copy, new SyncOptions(Adopt: ["local"]), cancellationToken));

            Assert.Contains("does not match this tree", failure.Message, StringComparison.Ordinal);

            var marker = await File.ReadAllTextAsync(Marker(copy), cancellationToken);

            Assert.Contains("\"Adopted\": true", marker, StringComparison.Ordinal);
            Assert.Contains("\"Completed\": false", marker, StringComparison.Ordinal);

            // And the next ordinary sync, which carries no adopt list, still asks.
            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("stopped before it finished", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A copy this tool made can gain a link afterwards — a build script pointing a directory at
    /// scratch space is the ordinary way — and from then on every build and every test writes
    /// through it, to somewhere outside the directory anybody named, reported as an ordinary write.
    /// The takeover list covers the first sync into somebody else's directory; this covers the rest.
    /// </summary>
    [Fact]
    public async Task AWriteThroughALink_IsSaidOnAnOrdinarySyncToo_NotOnlyWhenTakingOver()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var elsewhere = Path.Combine(temp.Path, "..", "elsewhere-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // A copy this tool made, so no takeover is involved anywhere below.
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Directory.CreateDirectory(elsewhere);
            Directory.Delete(Path.Combine(copy, "src"), recursive: true);

            if (!TryLink(Path.Combine(copy, "src"), elsewhere))
            {
                Assert.Skip("Creating a link needs a privilege this machine did not grant.");
            }

            // The writes land wherever the link points, so afterwards the copy does not hold what
            // this tree holds and the verification says so. The warning is what explains why.
            var failure = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken));

            Assert.Contains("does not match this tree", failure.Message, StringComparison.Ordinal);

            var said = harness.StandardError.ToString();

            Assert.Contains("writing through 'src'", said, StringComparison.Ordinal);
            Assert.Contains("is not in any list", said, StringComparison.Ordinal);

            // And what the link points at holds the files, outside the directory anybody named.
            Assert.True(File.Exists(Path.Combine(elsewhere, "a.c")));
        }
        finally
        {
            DeleteIfPresent(copy);
            DeleteIfPresent(elsewhere);
        }
    }

    /// <summary>
    /// One sync reaches every host, and one <c>--adopt</c> list is put to each of them in turn. The
    /// list has to select: naming one host must take over that host's directory and leave every
    /// other refused, because a flag that meant "go ahead" would pre-authorise taking over whatever
    /// unexpected directory sits at another host's repositoryPath, a mistyped one included.
    /// </summary>
    [Fact]
    public async Task OneAdoptList_TakesOverTheHostItNames_AndRefusesTheOneItDoesNot()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var named = Path.Combine(temp.Path, "..", "named-" + Guid.NewGuid().ToString("N")[..8]);
        var other = Path.Combine(temp.Path, "..", "other-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            foreach (var copy in new[] { named, other })
            {
                await MirrorAsync(temp.Path, copy, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(copy, "theirs.txt"), "x\n", cancellationToken);
            }

            // The one list a whole run carries, put to each host exactly as the loop does.
            var options = new SyncOptions(Adopt: ["ssh one"]);

            var taken = await service.SyncAsync(
                temp.Path,
                new RecordingTransport(Transport(harness), reports: HostId.Ssh("one")),
                named,
                options,
                cancellationToken);

            Assert.True(taken.Verified);
            Assert.False(File.Exists(Path.Combine(named, "theirs.txt")), "the named host was not taken over");

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                temp.Path,
                new RecordingTransport(Transport(harness), reports: HostId.Ssh("two")),
                other,
                options,
                cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("--adopt \"ssh two\"", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(other, "theirs.txt")), "a host nobody named lost files");
        }
        finally
        {
            DeleteIfPresent(named);
            DeleteIfPresent(other);
        }
    }

    /// <summary>
    /// A copy is created as a finished one or as a takeover that has begun, and never as unmarked.
    /// Unmarked means there is no marker at all; written down it becomes a marker claiming the copy
    /// was both taken over and finished, which is the most permissive thing the file can say about a
    /// directory nothing was ever taken over in.
    /// </summary>
    [Fact]
    public void ACopyIsNeverCreatedAsUnmarked_BecauseThereIsNoSuchMarker()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var copy = temp.Combine("copy");

        // Refused before it returns a task at all, because it is a caller's mistake rather than a
        // condition of the machine.
        var transport = Transport(harness);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = transport.CreateRootAsync(copy, CopyMark.None, TestContext.Current.CancellationToken);
        });

        // Refused before anything was made, so a caller that gets it wrong leaves nothing behind.
        Assert.False(Directory.Exists(copy));
    }

    /// <summary>
    /// Whether the copy is there and what its marker says are one question. Asked as two they are
    /// two round trips over ssh for something the far side answers in one, and the directory can
    /// change between them — so the mark that decides whether a sync may delete could be describing
    /// a directory other than the one that was found to exist.
    /// </summary>
    [Fact]
    public async Task WhetherTheCopyIsThere_AndWhatItsMarkerSays_AreAskedAsOneQuestion()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var counting = new RecordingTransport(Transport(harness));

        try
        {
            // One sync that creates the copy and one that finds it already there: the two paths
            // through the question.
            await service.SyncAsync(temp.Path, counting, copy, new SyncOptions(), cancellationToken);
            await service.SyncAsync(temp.Path, counting, copy, new SyncOptions(), cancellationToken);

            Assert.Equal(2, counting.Inspections);
            Assert.Equal(0, counting.RootExistsAsked);
            Assert.Equal(0, counting.MarksAsked);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A manifest holds files, so a plan can delete every file a directory had and never mention
    /// the directory. Locally that is invisible — git rm takes the directory with the last file —
    /// and it shows up only on a host, where the husk stays and whatever reads the tree next finds
    /// a directory with nothing addressable in it. Measured on a consumer's host after a wave of
    /// twenty deletions: ten directories left behind, eight holding nothing at all.
    /// </summary>
    [Fact]
    public async Task ADirectoryTheDeletionEmptied_IsRemoved_AndSaidSo()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await MirrorAsync(temp.Path, copy, cancellationToken);

            // A directory the source does not have at all, holding only files.
            Directory.CreateDirectory(Path.Combine(copy, "scripts", "retired"));
            await File.WriteAllTextAsync(Path.Combine(copy, "scripts", "retired", "a.sh"), "x\n", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(copy, "scripts", "retired", "b.sh"), "y\n", cancellationToken);

            await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["local"]), cancellationToken);

            Assert.False(Directory.Exists(Path.Combine(copy, "scripts", "retired")));

            // And its parent, which emptied with it.
            Assert.False(Directory.Exists(Path.Combine(copy, "scripts")));
            Assert.Contains("removed scripts/retired/", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A deletion in a directory that still holds the source's own files empties nothing: the directory
    /// stays, as it must, and nothing says it held only files the sync does not manage. A consumer was
    /// told exactly that of a directory whose files it then found identical to the source's.
    /// </summary>
    [Fact]
    public async Task ADeletionBesideTheSourcesOwnFiles_EmptiesNothing_AndSaysNothingOfIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await MirrorAsync(temp.Path, copy, cancellationToken);

            // A file the source no longer has, beside two it has.
            await File.WriteAllTextAsync(Path.Combine(copy, "src", "retired.c"), "r\n", cancellationToken);

            await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["local"]), cancellationToken);

            Assert.False(File.Exists(Path.Combine(copy, "src", "retired.c")));
            Assert.True(File.Exists(Path.Combine(copy, "src", "a.c")));
            Assert.DoesNotContain("does not manage", harness.StandardError.ToString() + harness.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A directory kept alive by something the walk never lists stays, and is named with what kept
    /// it. A link is the sharp case: no manifest holds one, so deciding emptiness from the plan
    /// would delete a directory still holding the one thing no plan can speak for. The same path
    /// answers for content sync.neverTransfer protects, which is what a consumer measured: two such
    /// directories survived a deletion wave and their next structural check went red naming one.
    /// <para>
    /// Staying is deliberate — removing a directory because its only remaining content is protected
    /// is one bad generalisation away from deleting a warm build root — but a divergence nobody is
    /// told about arrives later with nothing connecting it to the sync that caused it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADirectoryHeldOpenByWhatSyncDoesNotManage_Stays_AndIsNamedWithWhatHeldIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);
        var elsewhere = Path.Combine(temp.Path, "..", "elsewhere-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await MirrorAsync(temp.Path, copy, cancellationToken);
            Directory.CreateDirectory(elsewhere);

            Directory.CreateDirectory(Path.Combine(copy, "scripts", "retired"));
            await File.WriteAllTextAsync(Path.Combine(copy, "scripts", "retired", "a.py"), "x\n", cancellationToken);

            if (!TryLink(Path.Combine(copy, "scripts", "retired", "cache"), elsewhere))
            {
                Assert.Skip("Creating a link needs a privilege this machine did not grant.");
            }

            await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(Adopt: ["local"]), cancellationToken);

            // It stayed, because it is not empty — and what it holds is what no plan could list.
            Assert.True(Directory.Exists(Path.Combine(copy, "scripts", "retired")));
            Assert.True(Directory.Exists(elsewhere));

            var said = harness.StandardError.ToString();

            Assert.Contains("scripts/retired/", said, StringComparison.Ordinal);
            Assert.Contains("cache", said, StringComparison.Ordinal);
            Assert.Contains("differs from this tree", said, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(copy);
            DeleteIfPresent(elsewhere);
        }
    }

    /// <summary>
    /// What a create request means when the two ends are not the same build. This is the only place
    /// the mark crosses as text, and reading it wrong writes down the wrong answer to the one
    /// question that decides whether a later sync asks before deleting.
    /// </summary>
    [Theory]
    [InlineData(new[] { "/host/repo" }, CopyMark.Complete)]
    [InlineData(new[] { "/host/repo", "" }, CopyMark.Complete)]
    [InlineData(new[] { "/host/repo", "Complete" }, CopyMark.Complete)]
    [InlineData(new[] { "/host/repo", "AdoptionStopped" }, CopyMark.AdoptionStopped)]
    public void AMarkACreateRequestCarries_IsReadAsItWasSpelled(string[] arguments, CopyMark expected)
        => Assert.Equal(expected, SyncServe.MarkIn(arguments));

    /// <summary>
    /// A mark that is spelled and is not one a copy can carry is refused, not read as a finished
    /// copy. Enum.TryParse answers yes to any number, so "7" and "0" both parse — and "0" is None,
    /// which would be written down as a copy both taken over and finished. Lower case is refused
    /// too: the spelling is exact on purpose, so the two ends cannot drift.
    /// </summary>
    [Theory]
    [InlineData("None")]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("complete")]
    [InlineData("Whatever")]
    public void AMarkThisBuildCannotRecord_IsRefused_NotReadAsAFinishedCopy(string spelled)
    {
        var refusal = Assert.Throws<HarnessException>(() => SyncServe.MarkIn(["/host/repo", spelled]));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("different builds", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mark goes out as a name and has to come back as one. As a number its meaning would be
    /// the order the members happen to be declared in, so inserting one would silently change what
    /// every answer already in flight means.
    /// </summary>
    [Fact]
    public void AMarkCrossesTheWireAsItsName_InBothDirections()
    {
        var written = SyncServe.Answer(new SyncInspectAnswer(Exists: true, CopyMark.AdoptionStopped));

        Assert.Contains("\"AdoptionStopped\"", written, StringComparison.Ordinal);
        Assert.Equal(CopyMark.AdoptionStopped, SyncServe.ReadAnswer<SyncInspectAnswer>(written)!.Mark);
    }

    /// <summary>
    /// Inside the harness's own directory, the actions are carried by the rule the rest of the tree
    /// follows and nothing else is: an action crosses unless ignored, each action's build and
    /// artifacts never do, and the directory's state - connection data, secrets, locks - never
    /// does whatever any list says. The walk has to be able to open the way down to the actions.
    /// </summary>
    [Theory]
    [InlineData(".harness-config", false)]
    [InlineData(".harness-config/runner", false)]
    [InlineData(".harness-config/runner/actions", false)]
    [InlineData(".harness-config/runner/actions/corpus/corpus.yml", false)]
    [InlineData(".harness-config/runner/actions/real-examples/c/probe/fixture.sql", false)]
    [InlineData(".harness-config/runner/actions/corpus/build/run1/leg/x.o", true)]
    [InlineData(".harness-config/runner/actions/group/corpus/artifacts/run1/leg/pack/p.txt", true)]
    [InlineData(".harness-config/runner/actions/corpus/Build/x.o", true)]
    [InlineData(".harness-config/config.json", true)]
    [InlineData(".harness-config/sshItems/vps/item.env", true)]
    [InlineData(".harness-config/runner/.secrets/ci.env", true)]
    [InlineData(".harness-config/runner/.env/values.env", true)]
    [InlineData(".harness-config/lock.json", true)]
    [InlineData("src/a.c", false)]
    public void TheHarnessDirectory_CarriesItsActions_AndNothingElse(string path, bool withheld)
    {
        var exclusions = new SyncExclusions(new SyncConfig { NeverTransfer = [] }, WorktreeSettings.DefaultRoot);

        Assert.Equal(withheld, exclusions.IsWithheldFromTransfer(path));

        // What is withheld is also never deleted: it is that machine's own.
        if (withheld)
        {
            Assert.True(exclusions.IsProtectedFromDeletion(path));
        }
    }

    /// <summary>
    /// An ignored file under an action stays home, as an ignored file anywhere else in the tree does:
    /// the actions are carried by the ordinary rule, and that rule includes git's ignore list.
    /// </summary>
    [Fact]
    public void AnIgnoredFileUnderAnAction_StaysHome()
    {
        var exclusions = new SyncExclusions(
            new SyncConfig(),
            WorktreeSettings.DefaultRoot,
            [".harness-config/runner/actions/corpus/local.env"]);

        Assert.True(exclusions.IsWithheldFromTransfer(".harness-config/runner/actions/corpus/local.env"));
        Assert.False(exclusions.IsWithheldFromTransfer(".harness-config/runner/actions/corpus/corpus.yml"));
    }

    /// <summary>
    /// A leg placed on a host runs its runner there, from the host's copy: an action that never
    /// crosses is a runner no remote leg can run. Measured before this: every file under the actions
    /// directory was withheld. A new action nobody has committed crosses too - it is the one a worktree
    /// most needs to try on a remote leg.
    /// </summary>
    [Fact]
    public async Task AnAction_CrossesToAHostsCopy_WhetherOrNotItIsCommitted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        temp.WriteFile(".harness-config/runner/actions/committed/committed.yml", "steps: []\n");
        await harness.CommitAllAsync(temp.Path, "an action", cancellationToken);
        temp.WriteFile(".harness-config/runner/actions/fresh/fresh.yml", "steps: []\n");

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.True(File.Exists(Path.Combine(copy, ".harness-config", "runner", "actions", "committed", "committed.yml")));
            Assert.True(File.Exists(Path.Combine(copy, ".harness-config", "runner", "actions", "fresh", "fresh.yml")));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// An action the tree no longer has is removed from the host, or a stale one would sit above a
    /// new grouped action there and be refused as an action inside an action. What the host's own
    /// runs left under it - its build and artifacts - stays, because it is that machine's.
    /// </summary>
    [Fact]
    public async Task AnActionTheTreeNoLongerHas_IsRemovedFromAHost_AndTheHostsOwnRunStateStays()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        temp.WriteFile(".harness-config/runner/actions/old/old.yml", "steps: []\n");
        await harness.CommitAllAsync(temp.Path, "an action", cancellationToken);

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            var old = Path.Combine(copy, ".harness-config", "runner", "actions", "old");
            Directory.CreateDirectory(Path.Combine(old, "build", "r1", "leg"));
            Directory.CreateDirectory(Path.Combine(old, "artifacts", "r1", "leg", "pack"));
            await File.WriteAllTextAsync(Path.Combine(old, "build", "r1", "leg", "x.o"), "o", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(old, "artifacts", "r1", "leg", "pack", "p.txt"), "kept", cancellationToken);

            // Moved under a group, which the stale file would otherwise sit above.
            File.Delete(temp.Combine(".harness-config", "runner", "actions", "old", "old.yml"));
            temp.WriteFile(".harness-config/runner/actions/group/new/new.yml", "steps: []\n");
            await harness.CommitAllAsync(temp.Path, "moved", cancellationToken);

            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.False(File.Exists(Path.Combine(old, "old.yml")), "the tree no longer has this action");
            Assert.True(File.Exists(Path.Combine(copy, ".harness-config", "runner", "actions", "group", "new", "new.yml")));
            Assert.True(File.Exists(Path.Combine(old, "build", "r1", "leg", "x.o")), "a host's own working space is its own");
            Assert.True(File.Exists(Path.Combine(old, "artifacts", "r1", "leg", "pack", "p.txt")), "a host's kept output is its own");
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// The harness's state on either side stays where it is: this tree's connection data is not
    /// sent, and the host's own locks and run records are not touched.
    /// </summary>
    [Fact]
    public async Task TheHarnessState_OnEitherSide_StaysOnItsMachine()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        temp.WriteFile(".harness-config/sshItems/vps/item.env", "SECRET=1\n");

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            var hostLock = Path.Combine(copy, ".harness-config", "lock.json");
            await File.WriteAllTextAsync(hostLock, "{}", cancellationToken);

            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.False(File.Exists(Path.Combine(copy, ".harness-config", "sshItems", "vps", "item.env")));
            Assert.True(File.Exists(hostLock), "the host's own lock is its own");
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// An ignored file whose name begins with a space stays on this machine. Its name was trimmed on
    /// the way in, withholding a file of the trimmed name that did not exist, and the ignored one
    /// itself was copied to the host.
    /// </summary>
    [Fact]
    public async Task AnIgnoredFileWhoseNameBeginsWithASpace_StaysOnThisMachine()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        await File.AppendAllTextAsync(Path.Combine(temp.Path, ".gitignore"), "*.env\n", cancellationToken);
        await harness.CommitAllAsync(temp.Path, "ignore env", cancellationToken);

        // At the root, where the space begins the whole path git lists.
        temp.WriteFile(" local.env", "SECRET=1\n");

        try
        {
            await service.SyncAsync(temp.Path, Transport(harness), copy, new SyncOptions(), cancellationToken);

            Assert.True(File.Exists(Path.Combine(copy, "src", "a.c")), "the tree itself was copied");
            Assert.False(File.Exists(Path.Combine(copy, " local.env")));
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// A worktree's legs on a host run with the worktree's configuration. The configuration placed
    /// on a host was read from the main checkout whatever tree was synced, so a worktree's remote legs
    /// ran a configuration the worktree did not have - the defect already fixed for commands run here,
    /// surviving in the sync.
    /// </summary>
    [Fact]
    public async Task AWorktreeSync_PlacesTheWorktreesOwnConfiguration()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var worktree = Path.GetFullPath(Path.Combine(temp.Path, "..", "wt-" + Guid.NewGuid().ToString("N")[..8]));
        var copy = Path.GetFullPath(Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]));

        try
        {
            await harness.RunGitAsync(temp.Path, ["worktree", "add", "--detach", worktree], cancellationToken);

            // A setting only the worktree has, written the way the tool writes its own file.
            harness.ConfigStore.Save(
                Path.Combine(worktree, ".harness-config", "config.json"),
                new HarnessConfig { Worktrees = new WorktreeSettings { MaxNameLength = 17 } });

            await service.SyncAsync(worktree, Transport(harness), copy, new SyncOptions(), cancellationToken);

            var placed = harness.ConfigStore.Load(Path.Combine(copy, ".harness-config", "config.json"));
            Assert.Equal(17, placed.Worktrees.MaxNameLength);
        }
        finally
        {
            DeleteIfPresent(copy);
            DeleteIfPresent(worktree);
        }
    }

    /// <summary>
    /// The command works on the host's copy of the tree it is typed in: the main checkout's at the host's
    /// repositoryPath, a worktree's in that worktree's own beside it - never the main checkout's, which another tree's
    /// legs may be building in.
    /// </summary>
    [Theory]
    [InlineData(false, "/home/pi/repo")]
    [InlineData(true, "/home/pi/repo.worktree-feature")]
    public async Task TheSync_GoesToTheHostsCopyOfTheTreeItIsTypedIn(bool inWorktree, string expected)
    {
        var harness = new HarnessFactory();
        var main = TestHost.TemporaryRoot;
        var here = inWorktree ? Path.Combine(main, ".harness-config", "worktrees", "feature") : main;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } } },
            Legs = { ["arm"] = HostDoubles.Leg("linux", "arm64") },
        };

        var loader = HostDoubles.Loader(config, here, main);
        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "linux", Processor = "x86_64" }
            : new HostReport { Host = host, Os = "linux", Processor = "arm64" });

        var service = new SyncService(
            loader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Transport(harness),
            Substitute.For<ISyncTransportFactory>(),
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var outcome = await service.SyncHostsAsync(
            here, null, new SyncOptions(DryRun: true), ["build/report.txt"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal([$"ssh pi: would bring back 1 named file(s) from '{expected}'"], outcome.Details);
    }

    /// <summary>
    /// A worktree's copy on a host is recorded before anything is written to it, so that deleting the worktree asks
    /// that host to remove it - a first sync that stops part way included, since it has made a copy all the same.
    /// The main checkout's copy, which deleting no worktree removes, is not recorded, and a dry run, which writes
    /// nothing, records nothing.
    /// </summary>
    [Fact]
    public async Task AWorktreesCopyOnAHost_IsRecordedBeforeAnythingIsWritten_AndTheMainCheckoutsIsNot()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var worktree = Path.GetFullPath(Path.Combine(temp.Path, "..", "wt-" + Guid.NewGuid().ToString("N")[..8]));
        var mainCopy = Path.GetFullPath(Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]));
        var name = HostCopies.NameOf(worktree);
        var worktreeCopy = HostCopies.ForWorktree(mainCopy, name);
        var stoppedCopy = HostCopies.ForWorktree(mainCopy + "-stopped", name);
        var dryCopy = HostCopies.ForWorktree(mainCopy + "-dry", name);
        var pi = HostId.Ssh("pi");
        var vps = HostId.Ssh("vps");

        try
        {
            await harness.RunGitAsync(temp.Path, ["worktree", "add", "--detach", worktree], cancellationToken);

            await service.SyncAsync(temp.Path, new RecordingTransport(Transport(harness), reports: pi), mainCopy, new SyncOptions(), cancellationToken);
            await service.SyncAsync(worktree, new RecordingTransport(Transport(harness), reports: pi), worktreeCopy, new SyncOptions(), cancellationToken);
            await service.SyncAsync(worktree, new RecordingTransport(Transport(harness), reports: HostId.Ssh("mac")), dryCopy, new SyncOptions(DryRun: true), cancellationToken);
            var dropped = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                worktree, new RecordingTransport(Transport(harness), reports: vps) { FailsWrite = 1 }, stoppedCopy, new SyncOptions(), cancellationToken));
            Assert.StartsWith("the link dropped writing", dropped.Message, StringComparison.Ordinal);

            var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
            var record = new HostCopyRecord(harness.FileSystem, harness.Platform.PathComparison);
            var recorded = record.Of(context.Layout, name);

            Assert.True(Directory.Exists(stoppedCopy));
            Assert.Equal([("ssh pi", worktreeCopy), ("ssh vps", stoppedCopy)], recorded.Select(entry => (entry.Host, entry.Path)));
            Assert.All(recorded, entry => Assert.True(record.SameTree(worktree, entry.Tree), entry.Tree));
            Assert.Empty(record.Of(context.Layout, HostCopies.NameOf(temp.Path)));
        }
        finally
        {
            DeleteIfPresent(mainCopy);
            DeleteIfPresent(worktreeCopy);
            DeleteIfPresent(stoppedCopy);
            DeleteIfPresent(dryCopy);
            DeleteIfPresent(worktree);
        }
    }

    /// <summary>
    /// A copy another worktree kept under the same name still holds is refused before anything is written to it:
    /// synced by both, each would replace the tree the other put there. The refusal names that worktree.
    /// </summary>
    [Fact]
    public async Task ACopyAnotherWorktreeOfTheNameHolds_IsRefused_BeforeAnythingIsWritten()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var worktree = Path.GetFullPath(Path.Combine(temp.Path, "..", "wt-" + Guid.NewGuid().ToString("N")[..8]));
        var copy = HostCopies.ForWorktree(Path.GetFullPath(Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8])), HostCopies.NameOf(worktree));
        var other = temp.Combine("elsewhere", Path.GetFileName(worktree));
        Directory.CreateDirectory(other);

        try
        {
            await harness.RunGitAsync(temp.Path, ["worktree", "add", "--detach", worktree], cancellationToken);
            var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
            new HostCopyRecord(harness.FileSystem, harness.Platform.PathComparison)
                .Claim(context.Layout, new HostCopyEntry(HostCopies.NameOf(worktree), "ssh pi", copy, other));

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncAsync(
                worktree, new RecordingTransport(Transport(harness), reports: HostId.Ssh("pi")), copy, new SyncOptions(), cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains($"is the copy of the worktree at '{other}'", refusal.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(copy));
        }
        finally
        {
            DeleteIfPresent(copy);
            DeleteIfPresent(worktree);
        }
    }

    /// <summary>
    /// Writes the source tree's own files into <paramref name="copy"/>, so what a sync would change
    /// there is a delta rather than the whole directory. The deletion bound measures against what the
    /// copy holds, so a copy holding none of the source is over any bound before the case under test
    /// is ever reached.
    /// </summary>
    /// <param name="source">The tree being synced from.</param>
    /// <param name="copy">The directory to fill.</param>
    /// <param name="cancellationToken">Stops the copying.</param>
    private static async Task MirrorAsync(string source, string copy, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(copy);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);

            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var destination = Path.Combine(copy, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, await File.ReadAllBytesAsync(file, cancellationToken), cancellationToken);
        }
    }

    /// <summary>
    /// Creates a link, answering whether this machine allowed it. Windows grants the privilege only
    /// under developer mode or elevation, and skipping wherever it is refused runs the test on every
    /// machine that can rather than only on the ones that are not Windows.
    /// </summary>
    private static bool TryLink(string path, string target, bool file = false)
    {
        try
        {
            if (file)
            {
                File.CreateSymbolicLink(path, target);
            }
            else
            {
                Directory.CreateSymbolicLink(path, target);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The far side makes a copy's index hold what it is told, through the real parser: what the asking
    /// machine's sync carried is what a build there fingerprints. A list it cannot read - null, naming a
    /// blank path, or not a list at all - is refused and touches nothing: read as none, it would unstage
    /// every file the copy has.
    /// </summary>
    [Fact]
    public async Task TheAgentIndexesExactlyWhatItIsTold_AndRefusesAListItCannotRead()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();

        var created = await harness.GitClient.RunAsync(temp.Path, ["init", "--quiet", "."], cancellationToken: token);
        Assert.True(created.Succeeded, created.FailureMessage);

        temp.WriteFile(Path.Combine("src", "a.c"), "a\n");
        temp.WriteFile("notes.txt", "n\n");

        async Task<IReadOnlyList<string>> IndexedAsync()
            => [.. (await harness.GitClient.ListIndexAsync(temp.Path, token)).Select(entry => entry.Path)];

        var result = await CliRunner.RunAsync(
            ["sync-serve", SyncServe.Index, temp.Path, SyncServe.CarryPaths(["src/a.c"])],
            token);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Equal(["src/a.c"], await IndexedAsync());

        foreach (var unreadable in new[] { "null", """["src/a.c", " "]""", "not a list" })
        {
            var refused = await CliRunner.RunAsync(["sync-serve", SyncServe.Index, temp.Path, unreadable], token);

            Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
            Assert.Contains("The files to index arrived in a shape this build cannot read", refused.StandardError, StringComparison.Ordinal);
        }

        Assert.Equal(["src/a.c"], await IndexedAsync());
    }

    /// <summary>
    /// The far side of every real sync. A host removes its own husks, so the operation has to
    /// survive the wire: an answer that never arrives is the asking machine reporting that nothing
    /// was removed when a directory went.
    /// </summary>
    [Fact]
    public async Task TheAgentRemovesADirectoryTheDeletionEmptied_AndSaysWhatItDid()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        Directory.CreateDirectory(temp.Combine("gone"));
        Directory.CreateDirectory(temp.Combine("kept"));
        await File.WriteAllTextAsync(temp.Combine("kept", "still-here.txt"), "x\n", token);

        var result = await CliRunner.RunAsync(
            ["sync-serve", SyncServe.Prune, temp.Path, "gone\nkept"],
            token);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        var answer = SyncServe.ReadAnswer<SyncPruneAnswer>(result.StandardOutput.Trim());

        Assert.NotNull(answer);

        var went = Assert.Single(answer.Directories, entry => entry.Path == "gone");
        var stayed = Assert.Single(answer.Directories, entry => entry.Path == "kept");

        Assert.True(went.Removed);
        Assert.False(stayed.Removed);
        Assert.Contains("still-here.txt", stayed.Held, StringComparer.Ordinal);

        Assert.False(Directory.Exists(temp.Combine("gone")));
        Assert.True(Directory.Exists(temp.Combine("kept")));
    }

    /// <summary>
    /// A dry run exits as the run it previews would. Printing the cost and exiting zero makes
    /// "this checkout needs taking over" indistinguishable from "everything is in step" to anything
    /// reading the code, which is what a dry run is for reading. This changed silently between two
    /// releases and a consumer found it.
    /// </summary>
    [Fact]
    public async Task ADryRunOverAnUnclaimedCopy_SaysARunWouldRefuseIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, service) = await PrepareAsync(temp, cancellationToken);
        var copy = Path.Combine(temp.Path, "..", "copy-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(copy);
            await File.WriteAllTextAsync(Path.Combine(copy, "theirs.txt"), "x\n", cancellationToken);

            var result = await service.SyncAsync(
                temp.Path, Transport(harness), copy, new SyncOptions(DryRun: true), cancellationToken);

            Assert.True(result.RequiresAdoption);

            // And a copy this tool made says the opposite, so the flag means something.
            var mine = Path.Combine(temp.Path, "..", "mine-" + Guid.NewGuid().ToString("N")[..8]);

            try
            {
                await service.SyncAsync(temp.Path, Transport(harness), mine, new SyncOptions(), cancellationToken);

                var again = await service.SyncAsync(
                    temp.Path, Transport(harness), mine, new SyncOptions(DryRun: true), cancellationToken);

                Assert.False(again.RequiresAdoption);
            }
            finally
            {
                DeleteIfPresent(mine);
            }
        }
        finally
        {
            DeleteIfPresent(copy);
        }
    }

    /// <summary>
    /// The side that is written reads its tree through the rule the sending side reads its own with:
    /// a host's connection data, lock and each action's scratch are never listed, so never offered
    /// for deletion nor described to the machine that asked, while its actions are - so one the tree
    /// no longer has can be removed there.
    /// </summary>
    [Fact]
    public async Task AHostsManifest_ListsItsActions_AndNeverItsOwnState()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        temp.WriteFile(Path.Combine("src", "a.c"), "int a;");
        temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"), "name: probe\n");
        temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", "probe", "build", "run-1", "leg", "x.o"), "x");
        temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", "probe", "artifacts", "run-1", "leg", "kept.txt"), "x");
        temp.WriteFile(Path.Combine(".harness-config", "sshItems", "vps", ".env"), "HOST=example.invalid\n");
        temp.WriteFile(Path.Combine(".harness-config", "lock.json"), "{}");

        var manifest = await Transport(harness).ReadManifestAsync(temp.Path, [], TestContext.Current.CancellationToken);

        Assert.Equal(
            [".harness-config/runner/actions/probe/probe.yml", "src/a.c"],
            manifest.Entries.Keys.Order(StringComparer.Ordinal));
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
            new LegsService(harness.ContextLoader, Substitute.For<IHostInspector>(), harness.Platform, harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
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
