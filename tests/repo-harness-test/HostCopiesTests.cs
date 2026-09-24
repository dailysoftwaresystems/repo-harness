using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// A copy per tree on each host: the main checkout's at the host's repositoryPath, each worktree's beside it, and a
/// worktree's copies removed from their hosts when it is deleted, where the harness made them.
/// </summary>
public sealed class HostCopiesTests
{
    private static readonly HostId Pi = HostId.Ssh("pi");

    private static readonly HostId Mac = HostId.Ssh("mac");

    /// <summary>
    /// The main checkout keeps the host's repositoryPath, and a worktree a copy of its own beside it, named for its
    /// directory as a worktree's name is spelt, whatever form the repositoryPath takes.
    /// </summary>
    [Theory]
    [InlineData("/home/dev/repo", "feature", "/home/dev/repo.worktree-feature")]
    [InlineData("/home/dev/repo/", "feature", "/home/dev/repo.worktree-feature")]
    [InlineData("~/src/repo", "fix-42", "~/src/repo.worktree-fix-42")]
    [InlineData(@"C:\build\repo\", "feature", @"C:\build\repo.worktree-feature")]
    [InlineData("/home/dev/repo", "My Feature_X", "/home/dev/repo.worktree-my-feature-x")]
    [InlineData("/home/dev/repo", "__", "/home/dev/repo.worktree-worktree")]
    public void AWorktreesCopy_IsBesideTheMainCheckouts_UnderItsDirectorysName(string repositoryPath, string directory, string expected)
    {
        using var temp = new TempDirectory();
        var layout = new HarnessLayout(temp.Path, temp.Path);

        Assert.Equal(repositoryPath, HostCopies.For(repositoryPath, layout, temp.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, HostCopies.For(repositoryPath, layout, temp.Combine(".harness-config", "worktrees", directory), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AHostDeclaringNoRepositoryPath_HasNowhereToKeepACopy()
    {
        var refusal = Assert.Throws<HarnessException>(() => HostCopies.RepositoryPathOf(new HarnessConfig(), Pi));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("ssh pi declares no repositoryPath", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The record keeps each copy once, answers for one name at a time, and forgets a copy once; it is kept in the main
    /// checkout in a directory that keeps itself out of git; and it is refused where it cannot be read, as the run
    /// lock is, rather than read as empty, which would forget every copy it held.
    /// </summary>
    [Fact]
    public async Task TheRecord_KeepsEachCopyOnce_InADirectoryGitNeverSees_AndIsRefusedWhereItCannotBeRead()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var feature = Entry("/home/dev/repo.worktree-feature", Tree(temp, "feature"));
        var other = new HostCopyEntry("other", "wsl Example-Linux", "~/repo.worktree-other", Tree(temp, "other"));

        Assert.Null(record.Claim(layout, feature));
        Assert.Null(record.Claim(layout, feature));
        Assert.Null(record.Claim(layout, other));

        Assert.Equal([feature], record.Of(layout, "feature"));
        Assert.Equal(HarnessLayout.SelfIgnoreRule, File.ReadAllText(layout.HostCopiesIgnoreFile));
        Assert.Equal(temp.Combine(".harness-config", "host-copies", HostCopyRecord.FileName), HostCopyRecord.PathOf(layout));

        record.Forget(layout, feature);

        Assert.Empty(record.Of(layout, "feature"));
        Assert.Equal([other], record.Of(layout, "other"));

        File.WriteAllText(HostCopyRecord.PathOf(layout), "not json");

        var refusal = Assert.Throws<HarnessException>(() => record.Of(layout, "other"));
        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("Delete it to forget the copies it records", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A copy is claimed for one tree: while another worktree kept under the same name still exists, a claim for the
    /// same copy is answered with that worktree and records nothing, as the two would sync over each other; once it
    /// is gone, its entry is taken over.
    /// </summary>
    [Fact]
    public async Task AClaim_IsRefusedWhileAnotherWorktreeOfTheNameExists_AndTakesOverOneThatIsGone()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var first = Tree(temp, "one", "feature");
        var second = Tree(temp, "two", "feature");
        var theFirsts = Entry("/home/dev/repo.worktree-feature", first);
        var theSeconds = Entry("/home/dev/repo.worktree-feature", second);

        Assert.Null(record.Claim(layout, theFirsts));
        Assert.Equal(first, record.Claim(layout, theSeconds));
        Assert.Equal([theFirsts], record.Of(layout, "feature"));

        Directory.Delete(first);

        Assert.Null(record.Claim(layout, theSeconds));
        Assert.Equal([theSeconds], record.Of(layout, "feature"));
    }

    /// <summary>
    /// Legs of one worktree synced to several hosts at once record their copies at once, and none is lost: each
    /// change is made under the record's machine-wide lock, on what the one before it wrote.
    /// </summary>
    [Fact]
    public async Task CopiesRecordedAtOnce_AreAllKept()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var tree = Tree(temp, "feature");
        var entries = Enumerable.Range(0, 8)
            .Select(index => new HostCopyEntry("feature", $"ssh host{index}", "/home/dev/repo.worktree-feature", tree))
            .ToList();

        await Task.WhenAll(entries.Select(entry => Task.Run(() => record.Claim(layout, entry), TestContext.Current.CancellationToken)));

        Assert.Equal(entries.ToHashSet(), record.Of(layout, "feature").ToHashSet());
    }

    /// <summary>
    /// The far side removes a whole copy only where the harness made it: one it took over was somebody's directory
    /// first, and one with no mark of the harness's is nothing it knows it may remove - unless it holds nothing at
    /// all, as a removal whose last step failed leaves it, when there is nothing in it anybody could lose.
    /// </summary>
    [Theory]
    [InlineData("made", CopyRemoval.Removed, false)]
    [InlineData("taken over", CopyRemoval.Adopted, true)]
    [InlineData("taken over, and finished", CopyRemoval.Adopted, true)]
    [InlineData("unmarked", CopyRemoval.NotACopy, true)]
    [InlineData("left empty", CopyRemoval.Removed, false)]
    [InlineData("absent", CopyRemoval.Absent, false)]
    public async Task TheFarSide_RemovesACopyOnlyWhereTheHarnessMadeIt(string what, CopyRemoval expected, bool stays)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var transport = Local(harness);
        var cancellationToken = TestContext.Current.CancellationToken;
        var copy = hosts.Combine("repo.worktree-feature");

        switch (what)
        {
            case "made":
                await transport.CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
                break;
            case "taken over":
                await transport.CreateRootAsync(copy, CopyMark.AdoptionStopped, cancellationToken);
                break;
            case "taken over, and finished":
                await transport.CreateRootAsync(copy, CopyMark.AdoptionStopped, cancellationToken);
                await transport.CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
                break;
            case "unmarked":
                Directory.CreateDirectory(copy);
                File.WriteAllText(Path.Combine(copy, "work.txt"), "somebody's");
                break;
            case "left empty":
                Directory.CreateDirectory(Path.Combine(copy, HarnessLayout.DirectoryName));
                break;
        }

        Assert.Equal(expected, await transport.RemoveCopyAsync(copy, cancellationToken));
        Assert.Equal(stays, Directory.Exists(copy));
    }

    /// <summary>
    /// A removal that stops part way - here, a directory the disk will not let go of - fails by name, and leaves what
    /// is left still marked as the harness's, because the marker goes last: asking again, once nothing holds it,
    /// removes the rest, where a remainder left unmarked would have to be left in place as somebody's. However the
    /// host's configuration spells the copy: a listing spells what it holds as the root was spelt, where a path built
    /// from the root is spelt as the machine spells one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACopyWhoseRemovalStopsPartWay_StaysMarked_AndAskingAgainRemovesTheRest(bool speltOtherwise)
    {
        using var hosts = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var copy = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(Path.Combine(copy, "build"));
        File.WriteAllText(Path.Combine(copy, "build", "held.o"), "held open");
        File.WriteAllText(Path.Combine(copy, "main.c"), "int main;");

        var root = !speltOtherwise ? copy
            : OperatingSystem.IsWindows() ? copy.Replace('\\', '/')
            : copy.Insert(copy.LastIndexOf('/'), "/");

        var failed = await Assert.ThrowsAsync<HarnessException>(
            () => Local(harness, new HoldsOpen(harness.FileSystem, "build")).RemoveCopyAsync(root, cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);
        Assert.Contains($"'{root}' could not be removed whole", failed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(copy, HarnessLayout.DirectoryName, LocalSyncTransport.MarkerFileName)));

        Assert.Equal(CopyRemoval.Removed, await Local(harness).RemoveCopyAsync(root, cancellationToken));
        Assert.False(Directory.Exists(copy));
    }

    /// <summary>
    /// A removal is asked of the harness on the host by the copy's own path, from the home directory - which is there
    /// when the directory the copy was kept in is gone, so the host can answer that the copy is not there either - and
    /// one the host never answered is that host being unavailable, so the copy stays recorded: never read as removed,
    /// or as not there.
    /// </summary>
    [Fact]
    public async Task ARemovalRequest_NamesTheCopy_AndOneNeverAnswered_IsTheHostUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string copy = "/home/dev/repo.worktree-feature";
        HostAgentRequest? asked = null;

        var answering = new ScriptedHostCommands((_, command) =>
        {
            asked = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput!, HostAgentProtocol.JsonOptions);
            command.OnOutputLine?.Invoke(SyncServe.Answer(new SyncRemoveAnswer(CopyRemoval.Removed)));
            return HostResults.Finished(command, HarnessExit.Success);
        });

        Assert.Equal(CopyRemoval.Removed, await Remote(answering).RemoveCopyAsync(copy, cancellationToken));
        Assert.Equal([SyncServe.CommandName, SyncServe.RemoveCopy, copy], asked?.Arguments);
        Assert.Equal("~", asked?.Directory);

        var silent = new ScriptedHostCommands((_, command) => HostResults.Finished(command, HarnessExit.Success));
        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Remote(silent).RemoveCopyAsync(copy, cancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);
        Assert.Contains($"did not answer whether it removed '{copy}'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The far side's removal answers as sync-serve, the command a remote sync's agent starts, answers every request.</summary>
    [Fact]
    public async Task SyncServe_AnswersARemovalRequest()
    {
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var copy = hosts.Combine("repo.worktree-feature");
        await Local(new HarnessFactory()).CreateRootAsync(copy, CopyMark.Complete, cancellationToken);

        var result = await CliRunner.RunAsync(["sync-serve", SyncServe.RemoveCopy, copy], cancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Equal(CopyRemoval.Removed, SyncServe.ReadAnswer<SyncRemoveAnswer>(result.StandardOutput.Trim())?.Removal);
        Assert.False(Directory.Exists(copy));
    }

    /// <summary>
    /// Deleting a worktree removes its copies from the hosts recorded as holding one, and says so; a copy that is not
    /// the harness's to remove is left, and forgotten; and a host that cannot be asked keeps its copy recorded, and the
    /// deletion fails, naming it, though the worktree is gone - so deleting it again, once the host answers, finishes
    /// the job.
    /// </summary>
    [Fact]
    public async Task DeletingAWorktree_RemovesItsCopiesFromItsHosts_AndAgainFinishesWhatAHostLeft()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var answering = new HashSet<HostId> { Pi };

        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        var theirs = hosts.Combine("pi-before", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(onMac, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "work.txt"), "somebody's");

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));
        record.Claim(layout, Entry(onMac, created.Path, Mac));
        record.Claim(layout, Entry(theirs, created.Path));

        var service = Service(harness, answering);
        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, deleted.Outcome.ExitCode);
        Assert.Equal(
            "Worktree 'feature' was deleted, and 1 of its copies on hosts are not yet dealt with: run 'dssharness delete-worktree feature' "
            + "once what keeps each is gone.",
            deleted.Outcome.Message);
        Assert.False(Directory.Exists(created.Path));
        Assert.Equal(
            [
                created.Path,
                $"ssh pi: removed its copy at '{onPi}'",
                $"ssh mac: its copy at '{onMac}' stays, and is still recorded: the host could not be reached: ssh said ssh: connect to host 192.0.2.10 port 22: Connection timed out",
                $"ssh pi: left '{theirs}' in place: nothing there says the harness made it",
            ],
            deleted.Outcome.Details);
        Assert.False(Directory.Exists(onPi));
        Assert.True(Directory.Exists(theirs));
        Assert.Equal([Entry(onMac, created.Path, Mac)], record.Of(layout, "feature"));

        var again = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, again.Outcome.ExitCode);
        Assert.StartsWith("Worktree 'feature' is gone already, and 1 of its copies on hosts are not yet dealt with", again.Outcome.Message, StringComparison.Ordinal);

        answering.Add(Mac);
        var finished = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(finished.Succeeded, finished.Outcome.Message);
        Assert.Equal("Worktree 'feature' is gone already; each copy of it left on a host is dealt with.", finished.Outcome.Message);
        Assert.Equal([$"ssh mac: removed its copy at '{onMac}'"], finished.Outcome.Details);
        Assert.False(Directory.Exists(onMac));
        Assert.Empty(record.Of(layout, "feature"));

        var nothing = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, nothing.Outcome.ExitCode);
        Assert.Equal("No worktree named 'feature'.", nothing.Outcome.Message);
    }

    /// <summary>
    /// Copies are kept under a worktree's name, and a worktree made by hand, or by another tool, outside the worktrees
    /// root can be kept under the same one. While it still exists its copies are left for it - deleting a name with no
    /// worktree in the root takes nothing of it - and once it is gone, deleting the name removes what it left.
    /// </summary>
    [Fact]
    public async Task AWorktreeOfTheNameElsewhere_KeepsItsCopiesWhileItExists()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var elsewhere = Tree(temp, "elsewhere", "feature");
        var onPi = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        Record(harness).Claim(layout, Entry(onPi, elsewhere));
        var service = Service(harness, new HashSet<HostId> { Pi });

        var kept = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, kept.Outcome.ExitCode);
        Assert.Equal(
            $"No worktree named 'feature' is here or in git's record. The copies kept under that name on hosts are the worktree's at '{elsewhere}', "
            + "which still exists, so they were left for it.",
            kept.Outcome.Message);
        Assert.True(Directory.Exists(onPi));

        Directory.Delete(elsewhere);
        var removed = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(removed.Succeeded, removed.Outcome.Message);
        Assert.Equal([$"ssh pi: removed its copy at '{onPi}', of the worktree that was at '{elsewhere}'"], removed.Outcome.Details);
        Assert.False(Directory.Exists(onPi));
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A copy a run is building in is never removed from under it: the removal takes the lock a leg takes, and a copy
    /// held stays, recorded, with the run that holds it named - and the deletion ends with the lock's own code. Once
    /// the run lets go, deleting the worktree again removes it.
    /// </summary>
    [Fact]
    public async Task ACopyARunIsUsing_Stays_UntilTheRunLetsGo()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var onPi = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(onPi, created.Path));

        var building = await Lock(harness).TryAcquireAsync(
            layout,
            new LockRequest { Host = Pi.ToString(), Tree = onPi, Variant = "linux-x86_64-debug", Scope = LockScope.TreeShared, RunId = RunId.New(), Command = "test" },
            cancellationToken);
        Assert.NotNull(building.Handle);

        var service = Service(harness, new HashSet<HostId> { Pi });
        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.Contains(deleted.Outcome.Details ?? [], line => line.StartsWith($"ssh pi: its copy at '{onPi}' stays, and is still recorded: ", StringComparison.Ordinal)
            && line.Contains("running 'test'", StringComparison.Ordinal));
        Assert.True(Directory.Exists(onPi));

        await building.Handle.DisposeAsync();
        var again = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(again.Succeeded, again.Outcome.Message);
        Assert.Equal([$"ssh pi: removed its copy at '{onPi}'"], again.Outcome.Details);
        Assert.False(Directory.Exists(onPi));
    }

    /// <summary>
    /// The keys a leg's copy is recorded, locked and removed under are the ones the leg itself uses: its tree's name
    /// as a worktree's is spelt, its host as the command line names it, and the copy LegRunPlan placed it in. A leg
    /// naming a worktree is synced as a run syncs it, a run's lock held as a run holds it, and deleting the worktree
    /// is refused that copy until the run lets go - then removes it, and forgets it.
    /// </summary>
    [Fact]
    public async Task ALegsCopy_IsRecordedLockedAndRemoved_UnderTheKeysTheLegItselfUses()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var layout = context.Layout;
        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        var placing = new HarnessContext(layout, new HarnessConfig
        {
            Worktrees = context.Config.Worktrees,
            SshItems = { "pi" },
            Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = hosts.Combine("repo") } } },
        });
        var selected = new SelectedLeg("remote", new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Worktree = "feature" });
        var report = new HostReport { Host = Pi, Os = "linux", Processor = "x86_64" };
        var leg = Assert.Single(LegRunPlan.From(
            placing, new LegsReport([new LegPlacement(selected, report, null)], [report], Named: false), harness.Platform, out _));

        await Sync(harness).SyncAsync(
            leg.TreeRoot, new RecordingTransport(Local(harness), reports: leg.Host.Host), leg.HostTreeRoot, new SyncOptions(), cancellationToken);

        var recorded = Assert.Single(Record(harness).Of(layout, "feature"));
        Assert.Equal((Pi.ToString(), leg.HostTreeRoot), (recorded.Host, recorded.Path));
        Assert.True(Record(harness).SameTree(leg.TreeRoot, recorded.Tree));

        var building = await Lock(harness).TryAcquireAsync(
            layout,
            new LockRequest
            {
                Host = leg.Host.Host.ToString(),
                Tree = leg.HostTreeRoot,
                Variant = leg.Variant.DirectoryName,
                Scope = LockScope.TreeShared,
                RunId = RunId.New(),
                Command = "test",
            },
            cancellationToken);
        Assert.NotNull(building.Handle);

        var service = Service(harness, new HashSet<HostId> { Pi });
        var refused = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.True(Directory.Exists(leg.HostTreeRoot));

        await building.Handle.DisposeAsync();
        var removed = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(removed.Succeeded, removed.Outcome.Message);
        Assert.False(Directory.Exists(leg.HostTreeRoot));
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A host that fails to remove its copy keeps it recorded, and the deletion ends with the highest code a copy was
    /// left with: here a failure there, over a host that could not be asked.
    /// </summary>
    [Fact]
    public async Task AHostThatFailsToRemoveItsCopy_KeepsItRecorded_AndTheDeletionEndsWithTheHighestCode()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        Directory.CreateDirectory(Path.Combine(onPi, "build"));

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(hosts.Combine("mac", "repo.worktree-feature"), created.Path, Mac));
        record.Claim(layout, Entry(onPi, created.Path));

        var service = Service(harness, new HashSet<HostId> { Pi }, new HoldsOpen(harness.FileSystem, "build"));
        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, deleted.Outcome.ExitCode);
        Assert.Contains("2 of its copies on hosts are not yet dealt with", deleted.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains(deleted.Outcome.Details ?? [], line => line.StartsWith($"ssh pi: its copy at '{onPi}' stays, and is still recorded: '{onPi}' could not be removed whole", StringComparison.Ordinal));
        Assert.Equal(2, record.Of(layout, "feature").Count);
        Assert.True(Directory.Exists(onPi));
    }

    /// <summary>
    /// A record that cannot be written as each copy goes loses nothing that was done: every copy removed is said to
    /// be, and to be still recorded, and the deletion fails with the record's refusal; asked again, each host finds
    /// nothing to remove, and the record is brought in step.
    /// </summary>
    [Fact]
    public async Task ARecordThatCannotBeWritten_LosesNoLineOfWhatWasDone()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(onMac, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(onPi, created.Path));
        Record(harness).Claim(layout, Entry(onMac, created.Path, Mac));

        var both = new HashSet<HostId> { Pi, Mac };
        var deleted = await Service(harness, both, record: new WritesNoRecord(harness.FileSystem)).DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.Contains("2 of its copies on hosts are not yet dealt with", deleted.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains(deleted.Outcome.Details ?? [], line => line.StartsWith($"ssh pi: removed its copy at '{onPi}', and it could not be forgotten here", StringComparison.Ordinal));
        Assert.Contains(deleted.Outcome.Details ?? [], line => line.StartsWith($"ssh mac: removed its copy at '{onMac}', and it could not be forgotten here", StringComparison.Ordinal));
        Assert.False(Directory.Exists(onPi));
        Assert.False(Directory.Exists(onMac));

        var again = await Service(harness, both).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(again.Succeeded, again.Outcome.Message);
        Assert.Equal([$"ssh pi: found nothing at '{onPi}' to remove", $"ssh mac: found nothing at '{onMac}' to remove"], again.Outcome.Details);
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A record that cannot be written where nothing is asked to remove a copy - a host nothing declares any more,
    /// whose entry is forgotten before any host is reached - leaves the copies after it dealt with all the same: the
    /// refusal is said as that one copy not yet dealt with, and counted among them, rather than ending the deletion
    /// over the copies it still had to ask about.
    /// </summary>
    [Fact]
    public async Task AFailureForOneCopy_LeavesTheRestDealtWith_AndIsSaidAsThatCopy()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var gone = HostId.Ssh("retired");
        const string onRetired = "/home/dev/repo.worktree-feature";
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        // The one that fails is recorded first, so the copy still to be asked about is the one lost if its refusal ends the asking.
        Record(harness).Claim(layout, Entry(onRetired, created.Path, gone));
        Record(harness).Claim(layout, Entry(onPi, created.Path));

        var deleted = await Service(harness, new HashSet<HostId> { Pi }, record: new WritesNoRecord(harness.FileSystem)).DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.Contains("2 of its copies on hosts are not yet dealt with", deleted.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains(
            deleted.Outcome.Details ?? [],
            line => line.StartsWith($"ssh retired: its copy at '{onRetired}' is not yet dealt with: ", StringComparison.Ordinal));
        Assert.Contains(
            deleted.Outcome.Details ?? [],
            line => line.StartsWith($"ssh pi: removed its copy at '{onPi}', and it could not be forgotten here", StringComparison.Ordinal));
        Assert.False(Directory.Exists(onPi));
    }

    /// <summary>
    /// An interruption while the hosts are asked stops the asking, says what was done before it - a copy removed then
    /// is no longer recorded, and nothing else would say it went - and leaves the rest recorded for the next request.
    /// </summary>
    [Fact]
    public async Task AnInterruptionWhileHostsAreAsked_SaysWhatWasDone_AndLeavesTheRestRecorded()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, interruption.Token);
        await Local(harness).CreateRootAsync(onMac, CopyMark.Complete, interruption.Token);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, interruption.Token);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onPi, created.Path));
        record.Claim(layout, Entry(onMac, created.Path, Mac));

        var inspector = new RecordingInspector(host =>
        {
            if (host == Mac)
            {
                interruption.Cancel();
            }

            return Answering(host);
        });

        var deleted = await Service(harness, inspector).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, interruption.Token);

        Assert.Equal(HarnessExit.Cancelled, deleted.Outcome.ExitCode);
        Assert.Equal(
            "Worktree 'feature' was deleted, and the interruption came before every host holding a copy of it had been asked to remove it: "
            + "run 'dssharness delete-worktree feature' to ask the rest.",
            deleted.Outcome.Message);
        Assert.Equal([created.Path, $"ssh pi: removed its copy at '{onPi}'"], deleted.Outcome.Details);
        Assert.Equal([Entry(onMac, created.Path, Mac)], record.Of(layout, "feature"));
        Assert.True(Directory.Exists(onMac));
    }

    /// <summary>
    /// A record is read, not trusted: a path that is not where a worktree's copy is kept - here the main checkout's
    /// own copy - is never asked to be removed, and is forgotten, said as that.
    /// </summary>
    [Fact]
    public async Task ARecordedPathThatIsNoWorktreesCopy_IsNeverRemoved()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var mainCopy = hosts.Combine("repo");
        await Local(harness).CreateRootAsync(mainCopy, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(mainCopy, created.Path));

        var deleted = await Service(harness, new HashSet<HostId> { Pi }).DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains(
            $"ssh pi: '{mainCopy}' is not where a worktree's copy is kept, so nothing was asked to remove it, and it is no longer recorded",
            deleted.Outcome.Details ?? []);
        Assert.True(Directory.Exists(mainCopy));
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A host neither the worktree's own configuration nor the one the command runs in declares cannot be reached, now
    /// or later, so its copy is not asked about: it is forgotten, and named as left there, rather than failing every
    /// deletion of the name for good.
    /// </summary>
    [Fact]
    public async Task AHostNoLongerDeclared_HasItsCopyForgotten_AndNamed()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var gone = HostId.Ssh("retired");
        const string copy = "/home/dev/repo.worktree-feature";

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(copy, created.Path, gone));

        var inspector = new RecordingInspector(Answering);
        var deleted = await Service(harness, inspector).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains(
            $"ssh retired: no configuration here declares it any more, so nothing was asked to remove its copy at '{copy}', which "
            + "stays there for you to remove, and is no longer recorded",
            deleted.Outcome.Details ?? []);
        Assert.Empty(inspector.Inspected);
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A host only the worktree's own branch declares is reached through the worktree's configuration, read before the
    /// worktree goes: the configuration the command runs in need not declare it, and its copy there is not forgotten
    /// as a host nothing declares.
    /// </summary>
    [Fact]
    public async Task AHostOnlyTheWorktreesOwnConfigurationDeclares_IsAskedThroughIt()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var gpu = HostId.Ssh("gpu");
        var onGpu = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(onGpu, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        harness.WriteConfig(created.Path, new HarnessConfig
        {
            Worktrees = new WorktreeSettings { Root = ".wt", PathBudgetReserve = 5, PathBudgetMargin = 2 },
            SshItems = { "pi", "mac", "gpu" },
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" },
                    ["mac"] = new SshHostConfig { RepositoryPath = "/Users/dev/repo" },
                    ["gpu"] = new SshHostConfig { RepositoryPath = "/srv/repo" },
                },
            },
        });
        Record(harness).Claim(layout, Entry(onGpu, created.Path, gpu));

        var inspector = new RecordingInspector(Answering);
        var deleted = await Service(harness, inspector).DeleteAsync(temp.Path, "feature", force: true, deleteEvidence: false, cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains($"ssh gpu: removed its copy at '{onGpu}'", deleted.Outcome.Details ?? []);
        Assert.Equal([gpu], inspector.Inspected);
        Assert.False(Directory.Exists(onGpu));
    }

    /// <summary>
    /// The record is read again under each copy's lock: a worktree of the same name elsewhere can claim a copy while
    /// the hosts before it are asked - as its own sync may, once the deleted worktree is gone - and a copy it holds
    /// then is left for it, recorded as its own, and never removed from under it.
    /// </summary>
    [Fact]
    public async Task ACopyAnotherWorktreeClaimsWhileTheHostsAreAsked_IsLeftForIt()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var record = Record(harness);
        var onMac = hosts.Combine("mac", "repo.worktree-feature");
        var onPi = hosts.Combine("pi", "repo.worktree-feature");
        await Local(harness).CreateRootAsync(onMac, CopyMark.Complete, cancellationToken);
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        record.Claim(layout, Entry(onMac, created.Path, Mac));
        record.Claim(layout, Entry(onPi, created.Path));
        var elsewhere = Tree(temp, "elsewhere", "feature");

        var inspector = new RecordingInspector(host =>
        {
            if (host == Mac)
            {
                Assert.Null(Record(harness).Claim(layout, Entry(onPi, elsewhere)));
            }

            return Answering(host);
        });

        var deleted = await Service(harness, inspector).DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains($"ssh mac: removed its copy at '{onMac}'", deleted.Outcome.Details ?? []);
        Assert.Contains($"ssh pi: left '{onPi}' in place: the worktree at '{elsewhere}' has claimed it since", deleted.Outcome.Details ?? []);
        Assert.True(Directory.Exists(onPi));
        Assert.Equal([Entry(onPi, elsewhere)], record.Of(layout, "feature"));
    }

    /// <summary>
    /// A worktree whose directory was removed by hand, which git still records, has its copies removed when its
    /// record is cleared: the deletion's last part runs on that path as on the ordinary one.
    /// </summary>
    [Fact]
    public async Task AWorktreeRemovedByHand_HasItsCopiesRemoved_WithGitsRecordOfIt()
    {
        using var temp = new TempDirectory();
        using var hosts = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var onPi = hosts.Combine("repo.worktree-feature");
        await Local(harness).CreateRootAsync(onPi, CopyMark.Complete, cancellationToken);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        Record(harness).Claim(layout, Entry(onPi, created.Path));
        harness.FileSystem.DeleteDirectory(created.Path);

        var deleted = await Service(harness, new HashSet<HostId> { Pi }).DeleteAsync(
            temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.True(deleted.Succeeded, deleted.Outcome.Message);
        Assert.Contains($"ssh pi: removed its copy at '{onPi}'", deleted.Outcome.Details ?? []);
        Assert.False(Directory.Exists(onPi));
        Assert.Empty(Record(harness).Of(layout, "feature"));
    }

    /// <summary>
    /// A record that cannot be read removes nothing, and says so: the worktree is still deleted, the command fails,
    /// and the record is left as it was. A name with nothing here or in git's record is then not said to be no
    /// worktree, since whether a host holds a copy of one cannot be told.
    /// </summary>
    [Fact]
    public async Task AnUnreadableRecord_RemovesNothing_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var layout = await LayoutAsync(harness, temp);
        var created = await harness.WorktreeService.CreateAsync(temp.Path, "feature", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);
        var path = HostCopyRecord.PathOf(layout);
        Directory.CreateDirectory(layout.HostCopiesDirectory);
        File.WriteAllText(path, "not json");
        var service = Service(harness, new HashSet<HostId> { Pi });

        var deleted = await service.DeleteAsync(temp.Path, "feature", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.StartsWith("Worktree 'feature' was deleted, and none of its copies on hosts was removed: ", deleted.Outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(created.Path));

        var unknown = await service.DeleteAsync(temp.Path, "nothing", force: false, deleteEvidence: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, unknown.Outcome.ExitCode);
        Assert.StartsWith(
            "No worktree named 'nothing' is here or in git's record, and whether a host holds a copy of one cannot be told: ",
            unknown.Outcome.Message,
            StringComparison.Ordinal);
        Assert.Equal("not json", File.ReadAllText(path));
    }

    /// <summary>A name with no worktree, nothing in git's record and no copy recorded is no worktree at all.</summary>
    [Fact]
    public async Task ANameWithNothingRecordedAnywhere_IsNoWorktree()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var deleted = await Service(harness, new HashSet<HostId>()).DeleteAsync(
            temp.Path, "nothing", force: false, deleteEvidence: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, deleted.Outcome.ExitCode);
        Assert.Equal("No worktree named 'nothing'.", deleted.Outcome.Message);
    }

    private static HostCopyEntry Entry(string path, string tree, HostId? host = null)
        => new("feature", (host ?? Pi).ToString(), path, tree);

    /// <summary>A directory standing for a worktree on this machine, made so that it exists.</summary>
    private static string Tree(TempDirectory temp, params string[] parts)
        => Directory.CreateDirectory(temp.Combine(["trees", .. parts])).FullName;

    private static HostCopyRecord Record(HarnessFactory harness) => new(harness.FileSystem, harness.Platform.PathComparison);

    private static HostReport Answering(HostId host)
        => new() { Host = host, Os = "linux", Processor = "x86_64", Session = new HostSession(new HostConnection { Host = host }, "dssharness") };

    private static WorktreeService Service(HarnessFactory harness, ISet<HostId> answering, IFileSystem? hostDisk = null, IFileSystem? record = null)
        => Service(
            harness,
            new RecordingInspector(host => answering.Contains(host)
                ? Answering(host)
                : new HostReport { Host = host, Reason = "the host could not be reached: ssh said ssh: connect to host 192.0.2.10 port 22: Connection timed out" }),
            hostDisk,
            record);

    private static WorktreeService Service(HarnessFactory harness, IHostInspector inspector, IFileSystem? hostDisk = null, IFileSystem? record = null)
        => new(
            harness.ContextLoader,
            harness.GitClient,
            harness.FileSystem,
            harness.PathBudget,
            harness.Platform,
            harness.Output,
            new HostCopyRemover(inspector, new LocalHosts(harness, hostDisk ?? harness.FileSystem), Lock(harness), record ?? harness.FileSystem, harness.Platform));

    private static RunLock Lock(HarnessFactory harness) => new(harness.FileSystem, harness.Output, harness.Identity);

    private static SyncService Sync(HarnessFactory harness)
        => new(
            harness.ContextLoader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            Local(harness),
            Substitute.For<ISyncTransportFactory>(),
            new LegsService(harness.ContextLoader, Substitute.For<IHostInspector>(), harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

    private static RemoteSyncTransport Remote(IHostCommandRunner commands)
        => new(
            HostId.Ssh("vps"),
            new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
            commands,
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));

    /// <summary>
    /// A repository whose worktrees fit this machine's path budget however deep its temporary directory is, and which
    /// declares the hosts its tests keep copies on.
    /// </summary>
    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig
            {
                Worktrees = new WorktreeSettings { Root = ".wt", PathBudgetReserve = 5, PathBudgetMargin = 2 },
                SshItems = { "pi", "mac" },
                Hosts = new HostsConfig
                {
                    Ssh =
                    {
                        ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" },
                        ["mac"] = new SshHostConfig { RepositoryPath = "/Users/dev/repo" },
                    },
                },
            });
        return harness;
    }

    private static async Task<HarnessLayout> LayoutAsync(HarnessFactory harness, TempDirectory temp)
        => (await harness.ContextLoader.LoadAsync(temp.Path, TestContext.Current.CancellationToken)).Layout;

    private static LocalSyncTransport Local(HarnessFactory harness, IFileSystem? disk = null)
        => new(disk ?? harness.FileSystem, new ManifestBuilder(harness.FileSystem, harness.Platform), harness.GitClient, harness.Platform);

    /// <summary>Every host's copies kept on this machine's disk, reached as the host they stand for.</summary>
    private sealed class LocalHosts(HarnessFactory harness, IFileSystem disk) : ISyncTransportFactory
    {
        public ISyncTransport For(HostReport host) => new RecordingTransport(Local(harness, disk), reports: host.Host);
    }

    /// <summary>A disk on which a directory of one name is held open, so it cannot be deleted.</summary>
    private sealed class HoldsOpen(IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override void DeleteDirectory(string path)
        {
            if (string.Equals(Path.GetFileName(path), held, StringComparison.Ordinal))
            {
                throw new IOException($"The process cannot access '{path}' because it is being used by another process.");
            }

            base.DeleteDirectory(path);
        }
    }

    /// <summary>A disk on which the record of host copies can be read, and not written: a full disk, say.</summary>
    private sealed class WritesNoRecord(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override void WriteAllTextAtomic(string path, string contents)
        {
            if (string.Equals(Path.GetFileName(path), HostCopyRecord.FileName, StringComparison.Ordinal))
            {
                throw new IOException("There is not enough space on the disk.");
            }

            base.WriteAllTextAtomic(path, contents);
        }
    }
}
