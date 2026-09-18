using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>How a host is named, and read back from its name.</summary>
public sealed class HostIdTests
{
    /// <summary>
    /// A host reads back as the host it was spelled from, which is how a machine dispatching a leg
    /// tells the host it sends it to which host that is.
    /// </summary>
    [Fact]
    public void AHost_ReadsBackAsItIsSpelled()
    {
        foreach (var host in new[] { HostId.Local, HostId.Wsl("Example-Linux"), HostId.Ssh("build-box") })
        {
            Assert.True(HostId.TryParse(host.ToString(), out var read));
            Assert.Equal(host, read);
        }
    }

    /// <summary>What names no host in the one spelling hosts have is no host, never a guess at one.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ssh")]
    [InlineData("local build-box")]
    [InlineData("ftp build-box")]
    [InlineData("SSH build-box")]
    public void WhatNamesNoHost_IsNoHost(string? text)
    {
        Assert.False(HostId.TryParse(text, out var host));
        Assert.Null(host);
    }
}
