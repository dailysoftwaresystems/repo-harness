using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Runs;

namespace RepoHarness.Tests;

/// <summary>Where a placed leg's tree is, here and in a host's copy of it.</summary>
public sealed class LegRunPlanTests
{
    private static readonly HostId Pi = HostId.Ssh("pi");

    private static readonly HarnessConfig Config = new()
    {
        SshItems = { "pi" },
        Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } } },
    };

    /// <summary>
    /// A leg on a host works in the host's copy of its own tree: the main checkout's at the host's repositoryPath,
    /// and a worktree's - the one the command runs in, or the one the leg names - in the worktree's own copy beside
    /// it, so that trees do not share a copy, or the lock on it.
    /// </summary>
    [Theory]
    [InlineData(false, null, "/home/pi/repo")]
    [InlineData(false, "feature", "/home/pi/repo.worktree-feature")]
    [InlineData(true, null, "/home/pi/repo.worktree-feature")]
    public void ALegOnAHost_WorksInTheHostsCopyOfItsOwnTree(bool inWorktree, string? namedWorktree, string expected)
    {
        using var temp = new TempDirectory();
        var worktree = temp.Combine(".harness-config", "worktrees", "feature");
        var layout = new HarnessLayout(inWorktree ? worktree : temp.Path, temp.Path);

        var leg = Assert.Single(Place(layout, Pi, here: null, namedWorktree));

        Assert.Equal(expected, leg.HostTreeRoot);
        PathAssert.Same(inWorktree || namedWorktree is not null ? worktree : temp.Path, leg.TreeRoot);
    }

    /// <summary>
    /// On the host a leg was sent to, its tree is the copy it was sent to, which is its worktree's own: the worktree
    /// the leg names is on the machine that sent it, and nothing in the copy is at that path.
    /// </summary>
    [Fact]
    public void ALegSentToAHost_RunsInTheCopyItWasSentTo_WhateverWorktreeItNames()
    {
        using var copy = new TempDirectory();
        var layout = new HarnessLayout(copy.Path, copy.Path);

        var leg = Assert.Single(Place(layout, HostId.Local, here: Pi, namedWorktree: "feature"));

        PathAssert.Same(copy.Path, leg.TreeRoot);
        PathAssert.Same(copy.Path, leg.HostTreeRoot);
    }

    private static IReadOnlyList<PlacedLeg> Place(HarnessLayout layout, HostId host, HostId? here, string? namedWorktree)
    {
        var selected = new SelectedLeg("remote", new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Worktree = namedWorktree });
        var report = new HostReport { Host = host, Os = "linux", Processor = "x86_64" };

        return LegRunPlan.From(
            new HarnessContext(layout, Config),
            new LegsReport([new LegPlacement(selected, report, null)], [report], Named: false) { Here = here },
            HostDoubles.Platform(PlatformId.Linux),
            out _);
    }
}
