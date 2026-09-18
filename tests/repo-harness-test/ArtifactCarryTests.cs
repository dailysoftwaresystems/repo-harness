using System.Globalization;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// Carrying one run's kept artifacts to a host, which is the half of a round trip that crosses a
/// machine. What it must never do is report a transfer for a run that kept nothing, because the
/// step that would have read the payload is on the other side of it.
/// </summary>
public sealed class ArtifactCarryTests
{
    private const string RunId = "20260917-100000-0a1b2c3d";

    /// <summary>A grouped action, because a grouped one is the case the walk has to reach.</summary>
    private const string Action = "real-examples/sqlite/roundtrip";

    /// <summary>The leg that produced what is carried.</summary>
    private const string Leg = "lin-gcc-release";

    /// <summary>
    /// The refusal has to come before the hosts are measured. Placed after, a repository whose legs
    /// all run on this machine answers "no host needs a copy" and a mistyped run id reads as a
    /// transfer that simply had nothing to do.
    /// </summary>
    [Fact]
    public async Task ARunThatKeptNothing_IsRefusedByName_EvenWithNoHostToCarryItTo()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, service) = await PrepareAsync(temp, cancellationToken);

        var outcome = await service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = "20200101-000000-deadbeef" },
            [],
            cancellationToken);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Contains("20200101-000000-deadbeef", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("has kept anything", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a run that did keep something is not refused, so the rule above is about the run rather
    /// than about the option. This is the positive half: the walk finds a grouped action's
    /// artifacts at whatever depth the author put them.
    /// </summary>
    [Fact]
    public async Task ARunThatKeptSomething_InAGroupedAction_IsFound()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, service) = await PrepareAsync(temp, cancellationToken);

        var kept = Path.Combine(
            temp.Path,
            HarnessLayout.ActionArtifactsRelative(Action, RunId, Leg),
            "pack");

        Directory.CreateDirectory(kept);
        await File.WriteAllTextAsync(Path.Combine(kept, "payload.txt"), "carried", cancellationToken);

        var outcome = await service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = RunId },
            [],
            cancellationToken);

        // No host to carry it to here, which is the honest answer once the run itself is found.
        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.DoesNotContain("has kept anything", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One direction or the other. Asked for both, which one this runs in is undecided, and picking
    /// either would be picking for the reader.
    /// </summary>
    [Fact]
    public async Task CarryingAndPullingAtOnce_IsRefused()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, service) = await PrepareAsync(temp, cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = RunId },
            ["some/file"],
            cancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("--artifact", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("--pull", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary sync is unaffected: the option is absent, so nothing about artifacts is asked or
    /// answered.
    /// </summary>
    [Fact]
    public async Task AnOrdinarySync_IsUnchangedByAnyOfThis()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, service) = await PrepareAsync(temp, cancellationToken);

        var outcome = await service.SyncHostsAsync(temp.Path, null, new SyncOptions(), [], cancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
    }

    /// <summary>
    /// A consuming step names the producer through the run's artifacts, which hold one directory per
    /// leg. This leg's own is the wrong half of the question for a round trip, whose whole point is
    /// that what was built on one machine runs on another.
    /// </summary>
    [Fact]
    public void AStepCanNameAnotherLegsArtifacts_ThroughTheRunsOwn()
    {
        var paths = new LegPaths("/tree", "/tree/build/v")
        {
            ActionBuild = "/tree/a/build/run1/lin",
            ActionArtifacts = "/tree/a/artifacts/run1/lin",
            RunArtifacts = "/tree/a/artifacts/run1",
        };

        Assert.Equal(
            "/tree/a/artifacts/run1/win-msvc-release/pack/payload.txt",
            LegPathNames.Expand("{runArtifacts}/win-msvc-release/pack/payload.txt", paths, "step"));

        // And this leg's own is still reachable, and is not the same directory.
        Assert.Equal("/tree/a/artifacts/run1/lin", LegPathNames.Expand("{actionArtifacts}", paths, "step"));
    }

    /// <summary>
    /// An empty name is the option asked for, not the option absent. Read as absent it would fall
    /// through to an ordinary sync, which deletes on every host whatever this tree does not have -
    /// so a script whose run id variable is unset would ask to carry one run's files and overwrite
    /// each host's checkout instead.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CarryingARunWithNoName_IsRefused_RatherThanReadAsNoArtifactAtAll(string unset)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, service) = await PrepareAsync(temp, cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = unset },
            [],
            cancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("--artifact", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("empty name", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the same empty name does not slip past the two-directions refusal either, which is the
    /// other way that value turns one command into a different one.
    /// </summary>
    [Fact]
    public async Task CarryingARunWithNoName_WhilePulling_IsStillRefused()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, service) = await PrepareAsync(temp, cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = string.Empty },
            ["some/file"],
            cancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("empty name", refusal.Message, StringComparison.Ordinal);

        // And on its own the two-directions refusal answers for it too, because what decides that
        // one is whether --artifact was given at all rather than what it was given.
        var both = Assert.Throws<HarnessException>(
            () => new SyncOptions { Artifact = string.Empty }.RefuseWhenPullingAndCarrying(["some/file"]));

        Assert.Contains("--pull", both.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What is under an action's build directory is the working space of a run in flight, or of one
    /// that was killed before it could clear its own. It can hold anything a build holds, a
    /// directory called artifacts among them, and none of it is what a run kept.
    /// </summary>
    [Fact]
    public async Task WhatSitsInsideAnActionsWorkingSpace_IsNotWhatTheRunKept()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (_, service) = await PrepareAsync(temp, cancellationToken);

        // A build tree left behind by a killed run, which happens to hold this shape inside it.
        var inside = Path.Combine(
            temp.Path,
            HarnessLayout.ActionBuildRelative(Action, RunId, Leg),
            "pack",
            HarnessLayout.ActionArtifactsDirectoryName,
            RunId,
            Leg);

        Directory.CreateDirectory(inside);
        await File.WriteAllTextAsync(Path.Combine(inside, "payload.txt"), "scratch", cancellationToken);

        var outcome = await service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = RunId },
            [],
            cancellationToken);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Contains("has kept anything", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dry run changes nothing. The carry was the one write to a host with no such branch, so a
    /// preview wrote the whole artifact set to every host and then reported that nothing had been
    /// changed - the one command whose contract is to change nothing, denying what it just did.
    /// </summary>
    [Fact]
    public async Task ADryRunCarriesNothing_AndSaysWhatItWouldHaveCarried()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var carriage = await WithAHostAsync(temp, cancellationToken);

        var outcome = await carriage.Service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions(DryRun: true) { Artifact = RunId },
            [],
            cancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Empty(carriage.Transport.Written);
        Assert.False(File.Exists(Path.Combine(carriage.Copy, KeptAt(0))));

        var said = Assert.Single(
            outcome.Details ?? [],
            line => line.Contains("would carry", StringComparison.Ordinal));

        Assert.Contains(RunId, said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The return leg of the same trip, and the same branch: a pull is the other one-way direction,
    /// and it had no dry-run branch either, so a preview read files off every host and wrote them
    /// into this tree before reporting that nothing had been changed.
    /// </summary>
    [Fact]
    public async Task ADryRunBringsNothingBack_AndSaysWhatItWouldHaveBrought()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var carriage = await WithAHostAsync(temp, cancellationToken);

        // Something a real pull would find, so what is asserted below is that it did not run rather
        // than that it could not.
        const string There = "out/report.txt";
        var over = Path.Combine(carriage.Copy, "out");
        Directory.CreateDirectory(over);
        await File.WriteAllTextAsync(Path.Combine(over, "report.txt"), "measured", cancellationToken);

        var outcome = await carriage.Service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions(DryRun: true),
            [There],
            cancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.False(File.Exists(Path.Combine(temp.Path, "out", "report.txt")));

        Assert.Contains(
            outcome.Details ?? [],
            line => line.Contains("would bring back", StringComparison.Ordinal));
    }

    /// <summary>
    /// A carry writes an existing copy's own files and makes no copy of its own. Left to create
    /// one it would fill in a mistyped repositoryPath rather than let anybody notice it, and the
    /// directory it made would carry no marker - so the next ordinary sync would refuse, as a
    /// directory the harness did not create, a directory this tool made itself minutes earlier.
    /// </summary>
    [Theory]
    [InlineData(false, CopyMark.None, "is not there")]
    [InlineData(true, CopyMark.None, "did not create")]
    [InlineData(true, CopyMark.AdoptionStopped, "stopped before it finished")]
    public async Task AHostWithNoCopyThisHarnessMade_IsRefused_AndNothingIsWrittenToIt(
        bool exists,
        CopyMark mark,
        string expected)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var carriage = await WithAHostAsync(temp, cancellationToken, answers: new SyncInspectAnswer(exists, mark));

        var outcome = await carriage.Service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = RunId },
            [],
            cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains("carried nowhere", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(expected, string.Join(" ", outcome.Details ?? []), StringComparison.Ordinal);

        // The whole point of asking before the loop: nothing reached the host at all.
        Assert.Empty(carriage.Transport.Written);
    }

    /// <summary>
    /// A copy this harness made is carried into, so the rule above is about the copy rather than
    /// about the option, and this is the round trip itself: the file lands at the same relative
    /// place, which is what makes a consuming step's path work unchanged over there.
    /// </summary>
    [Fact]
    public async Task ARunIsCarriedToAHost_AtTheSameRelativePlace()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var carriage = await WithAHostAsync(temp, cancellationToken);

        var outcome = await carriage.Service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = RunId },
            [],
            cancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        Assert.Equal(
            "carried",
            await File.ReadAllTextAsync(Path.Combine(carriage.Copy, KeptAt(0)), cancellationToken));
    }

    /// <summary>
    /// A carry lands whole or not at all. Two thirds of what the producer kept is the same
    /// directory holding fewer files: a step reading it measures less than was built and passes,
    /// and nothing anywhere says which of the two happened. The transfer stops loudly on this
    /// side either way; taking the part back is what stops it being quiet on the other.
    /// </summary>
    [Fact]
    public async Task ACarryThatStoppedPartWay_TakesBackWhatItHadAlreadyWritten()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Three files, and the link drops on the third.
        var carriage = await WithAHostAsync(temp, cancellationToken, count: 3, failsWrite: 3);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => carriage.Service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = RunId },
            [],
            cancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, refusal.ExitCode);

        Assert.Equal(2, carriage.Transport.Written.Count);
        Assert.Equal(carriage.Transport.Written, carriage.Transport.Deleted);

        foreach (var written in carriage.Transport.Written)
        {
            Assert.False(
                File.Exists(Path.Combine(carriage.Copy, written)),
                $"'{written}' was carried before the link dropped and should have been taken back");
        }
    }

    /// <summary>
    /// And when the cleanup cannot finish either, what stayed is named. This is the one outcome
    /// where a later step can read a run's artifacts and be measuring less than the producer kept,
    /// so it is the one that must not be quiet.
    /// </summary>
    [Fact]
    public async Task ACarryThatCouldNotBeTakenBack_NamesWhatIsStillThere()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var carriage = await WithAHostAsync(
            temp,
            cancellationToken,
            count: 3,
            failsWrite: 3,
            refusesToDelete: true);

        _ = await Assert.ThrowsAsync<HarnessException>(() => carriage.Service.SyncHostsAsync(
            temp.Path,
            null,
            new SyncOptions { Artifact = RunId },
            [],
            cancellationToken));

        var said = carriage.Harness.StandardError.ToString();

        Assert.Contains("could not be taken back", said, StringComparison.Ordinal);
        Assert.Contains("payload-0.txt", said, StringComparison.Ordinal);
        Assert.Contains("payload-1.txt", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file larger than one request can hold is refused by name and with its size. Left to
    /// throw, it is an OutOfMemoryException, which every command reports as a defect in the tool -
    /// and a build output of that size is ordinary.
    /// </summary>
    [Fact]
    public void AFileTooLargeToCrossWhole_IsRefusedByName_RatherThanRunningOutOfMemory()
    {
        var refusal = Assert.Throws<HarnessException>(() => SyncServe.RefuseAFileTooLargeToCarry(
            SyncServe.LargestFile + 1,
            "build/pack/corpus.bin",
            "ssh vps"));

        Assert.Contains("corpus.bin", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ssh vps", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("one request", refusal.Message, StringComparison.Ordinal);

        // And everything up to the bound crosses, so the bound is the encoding's and not a policy.
        SyncServe.RefuseAFileTooLargeToCarry(SyncServe.LargestFile, "build/pack/corpus.bin", "ssh vps");
    }

    /// <summary>A run's artifacts here, a host holding a copy of this tree, and what joins them.</summary>
    /// <param name="Harness">The services, and the streams a warning is written to.</param>
    /// <param name="Service">The sync under test.</param>
    /// <param name="Transport">What the host was asked, and what it was given.</param>
    /// <param name="Copy">Where the host keeps its copy.</param>
    private sealed record Carriage(
        HarnessFactory Harness,
        ISyncService Service,
        RecordingTransport Transport,
        string Copy);

    /// <summary>Where the run kept its <paramref name="index"/>th file, relative to the tree root.</summary>
    /// <param name="index">Which of the kept files.</param>
    private static string KeptAt(int index) => PathPatterns.Normalize(Path.Combine(
        HarnessLayout.ActionArtifactsRelative(Action, RunId, Leg),
        "pack",
        $"payload-{index.ToString(CultureInfo.InvariantCulture)}.txt"));

    /// <summary>
    /// A tree whose run kept <paramref name="count"/> files, and one ssh host a leg of it runs on.
    /// </summary>
    /// <param name="temp">The tree.</param>
    /// <param name="cancellationToken">Stops the setup.</param>
    /// <param name="answers">What the host should say about its copy, or null to let it look.</param>
    /// <param name="count">How many files the run kept.</param>
    /// <param name="failsWrite">Which write the link drops on, counting from one, or zero for none.</param>
    /// <param name="refusesToDelete">Whether the cleanup after a dropped link can finish.</param>
    /// <remarks>
    /// The host's side is a real transport against a directory here, so what these tests assert
    /// about a copy is what a copy actually became rather than what a stand-in was told to say.
    /// </remarks>
    private static async Task<Carriage> WithAHostAsync(
        TempDirectory temp,
        CancellationToken cancellationToken,
        SyncInspectAnswer? answers = null,
        int count = 1,
        int failsWrite = 0,
        bool refusesToDelete = false)
    {
        var harness = new HarnessFactory();

        // Beside the tree rather than inside it, as a host's copy is, and spelled without the
        // '..' the configuration refuses in a repositoryPath.
        var copy = Path.GetFullPath(Path.Combine(temp.Path, "..", $"copy-{Guid.NewGuid().ToString("N")[..8]}"));

        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "a.c"), "a\n", cancellationToken);

        await harness.InitializeHarnessAsync(
            temp.Path,
            cancellationToken,
            new HarnessConfig
            {
                BuildConfigs = { ["debug"] = new BuildConfiguration() },
                Hosts = new HostsConfig { Ssh = { ["vps"] = new SshHostConfig { RepositoryPath = copy } } },
                SshItems = { "vps" },
                Legs = { ["arm"] = HostDoubles.Leg("linux", "arm64") },
            });

        await harness.CommitAllAsync(temp.Path, "initial", cancellationToken);

        var kept = Path.Combine(temp.Path, HarnessLayout.ActionArtifactsRelative(Action, RunId, Leg), "pack");
        Directory.CreateDirectory(kept);

        for (var index = 0; index < count; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(kept, $"payload-{index.ToString(CultureInfo.InvariantCulture)}.txt"), "carried", cancellationToken);
        }

        var local = new LocalSyncTransport(
            harness.FileSystem,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            harness.GitClient,
            harness.Platform);

        // A copy this harness made, which is what a carry requires, unless a test says otherwise.
        await local.CreateRootAsync(copy, CopyMark.Complete, cancellationToken);

        var transport = new RecordingTransport(local, reports: HostId.Ssh("vps"))
        {
            Answers = answers,
            FailsWrite = failsWrite,
            RefusesToDelete = refusesToDelete,
        };

        var factory = Substitute.For<ISyncTransportFactory>();
        factory.For(Arg.Any<HostReport>()).Returns(transport);

        var service = new SyncService(
            harness.ContextLoader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            local,
            factory,
            new LegsService(
                harness.ContextLoader,
                new RecordingInspector(host => host.Kind == HostKind.Local
                    ? new HostReport { Host = host, Os = "windows", Processor = "x86_64" }
                    : new HostReport { Host = host, Os = "linux", Processor = "arm64" }),
                harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        return new Carriage(harness, service, transport, copy);
    }

    private static async Task<(HarnessFactory Harness, ISyncService Service)> PrepareAsync(
        TempDirectory temp,
        CancellationToken cancellationToken)
    {
        var harness = new HarnessFactory();

        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "a.c"), "a\n", cancellationToken);

        await harness.InitializeHarnessAsync(temp.Path, cancellationToken, new HarnessConfig());
        await harness.CommitAllAsync(temp.Path, "initial", cancellationToken);

        var service = new SyncService(
            harness.ContextLoader,
            new ManifestBuilder(harness.FileSystem, harness.Platform),
            new LocalSyncTransport(
                harness.FileSystem,
                new ManifestBuilder(harness.FileSystem, harness.Platform),
                harness.GitClient,
                harness.Platform),
            Substitute.For<ISyncTransportFactory>(),
            new LegsService(harness.ContextLoader, Substitute.For<IHostInspector>(), harness.Output),
            harness.GitClient,
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        return (harness, service);
    }
}
