using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>What each host declares for itself.</summary>
public sealed class HostsConfigTests
{
    /// <summary>
    /// Each host is given its own section, found whatever case its name is spelled in, and a host
    /// with no section declares nothing.
    /// </summary>
    [Fact]
    public void EachHost_IsGivenItsOwnSection_AndOneWithNoneDeclaresNothing()
    {
        var hosts = new HostsConfig
        {
            Local = new LocalHostConfig { BuildCores = 7 },
            Wsl = { ["Example-Linux"] = new WslHostConfig { RepositoryPath = "~/repo", BuildCores = 5 } },
            Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/repo", BuildCores = 3 } },
        };

        Assert.Equal(7, hosts.SettingsFor(HostId.Local).BuildCores);
        Assert.Equal(5, hosts.SettingsFor(HostId.Wsl("example-linux")).BuildCores);
        Assert.Equal(3, hosts.SettingsFor(HostId.Ssh("PI")).BuildCores);

        var undeclared = hosts.SettingsFor(HostId.Ssh("elsewhere"));

        Assert.Null(undeclared.BuildCores);
        Assert.Empty(undeclared.Env);
    }
}
