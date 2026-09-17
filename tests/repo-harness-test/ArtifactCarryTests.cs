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
            HarnessLayout.ActionArtifactsRelative("real-examples/sqlite/roundtrip", RunId, "lin-gcc-release"),
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
