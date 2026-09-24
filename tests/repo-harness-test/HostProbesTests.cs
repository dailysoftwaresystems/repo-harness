using RepoHarness.Core.Hosts;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// What programs on a host print is read before a leg is placed there. A reader that guessed at text
/// it did not recognise would place a leg on a host that cannot run it.
/// </summary>
public sealed class HostProbesTests
{
    [Theory]
    [InlineData("Linux x86_64\n", "linux", "x86_64")]
    [InlineData("Linux aarch64", "linux", "arm64")]
    [InlineData("Darwin arm64\n", "macos", "arm64")]
    public void ReadUname_TranslatesTheSystemAndTheMachine(string output, string os, string processor)
    {
        var (actualOs, actualProcessor) = HostProbes.ReadUname(output);

        Assert.Equal(os, actualOs);
        Assert.Equal(processor, actualProcessor);
    }

    [Fact]
    public void ReadUname_ReadsNothing_FromTextThatIsNotItsOutput()
    {
        var (os, processor) = HostProbes.ReadUname("uname: command not found");

        Assert.Null(os);
        Assert.Null(processor);
    }

    [Fact]
    public void ReadSdks_ReadsEverySdk_AndSkipsABannerBeforeThem()
    {
        const string Output = "Welcome to .NET!\n---------------------\n8.0.414 [/usr/lib/dotnet/sdk]\n10.0.100 [/usr/lib/dotnet/sdk]\n";

        var sdks = HostProbes.ReadSdks(Output);

        Assert.Equal(["8.0.414", "10.0.100"], sdks.Select(sdk => sdk.Version));
        Assert.Equal([8, 10], sdks.Select(sdk => sdk.Major));
        Assert.All(sdks, sdk => Assert.False(sdk.OnWindows));
    }

    [Fact]
    public void ReadSdks_TellsAWindowsInstallation_ByItsPath()
    {
        var sdk = Assert.Single(HostProbes.ReadSdks("10.0.401 [C:\\Program Files\\dotnet\\sdk]\r\n"));

        Assert.True(sdk.OnWindows);
        Assert.Equal(10, sdk.Major);
    }

    [Fact]
    public void TryReadToolVersion_FindsTheTool_WhateverTheCaseOfItsId()
    {
        const string Output = """
            {"version":1,"data":[{"packageId":"dotnet-dump","version":"9.0.1","commands":["dotnet-dump"]},{"packageId":"dssharness","version":"0.2.0-beta","commands":["DssHarness"]}]}
            """;

        Assert.True(HostProbes.TryReadToolVersion(Output, "DssHarness", out var version));
        Assert.Equal("0.2.0-beta", version);
    }

    [Fact]
    public void TryReadToolVersion_ReadsATool_ThatIsNotInstalled()
    {
        Assert.True(HostProbes.TryReadToolVersion("""{"version":1,"data":[]}""", "DssHarness", out var version));
        Assert.Null(version);
    }

    [Fact]
    public void TryReadToolVersion_FindsTheDocument_AfterABanner()
    {
        const string Output = "Welcome to .NET!\n{\"version\":1,\"data\":[{\"packageId\":\"dssharness\",\"version\":\"1.0.0\"}]}";

        Assert.True(HostProbes.TryReadToolVersion(Output, "DssHarness", out var version));
        Assert.Equal("1.0.0", version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tool list failed")]
    [InlineData("{ not json")]
    [InlineData("""{"version":1}""")]
    public void TryReadToolVersion_RefusesTextThatIsNotTheListing(string output)
    {
        Assert.False(HostProbes.TryReadToolVersion(output, "DssHarness", out _));
    }

    [Fact]
    public void IsMissingDistribution_ReadsTheErrorCode_WhateverLanguageTheSentenceIsIn()
    {
        // Measured on a Windows set to Portuguese: the sentence is translated, the code is not.
        Assert.True(HostProbes.IsMissingDistribution(
            "Não há distribuição com o nome fornecido.\nCódigo de erro: Wsl/Service/WSL_E_DISTRO_NOT_FOUND"));
        Assert.False(HostProbes.IsMissingDistribution("Linux x86_64"));
    }

    [Fact]
    public void IsMissingProgramInWsl_ReadsWhatWslPrints_ForAProgramTheDistributionLacks()
    {
        // Measured: what WSL prints when --exec names a program the distribution does not have.
        const string Output = "<3>WSL (708) ERROR: CreateProcessCommon:559: execvpe(dotnet) failed: No such file or directory";

        Assert.True(HostProbes.IsMissingProgramInWsl(Output, "dotnet"));
        Assert.False(HostProbes.IsMissingProgramInWsl(Output, "uname"));
    }

    [Theory]
    [InlineData("bash\nsshd\nDssHarness\n", true)]
    [InlineData("/Users/dev/.dotnet/tools/dssharness\n", true)]
    [InlineData("\"svchost.exe\",\"1234\",\"Services\",\"0\",\"10,000 K\"\r\n\"DssHarness.exe\",\"4321\",\"Console\",\"1\",\"50,000 K\"\r\n", true)]
    [InlineData("bash\nDssHarness-helper\nsshd\n", false)]
    [InlineData("\"svchost.exe\",\"1234\",\"Services\",\"0\",\"10,000 K\"\r\n", false)]
    public void ListsProcess_FindsTheTool_InEitherKindOfListing(string listing, bool expected)
    {
        Assert.Equal(expected, HostProbes.ListsProcess(listing, "DssHarness"));
    }

    [Fact]
    public void Excerpt_KeepsTheEndOfALongOutput_OnOneLine()
    {
        var output = string.Join("\n", Enumerable.Range(1, 200).Select(index => $"line {index}")) + "\nthe actual error";

        var excerpt = HostProbes.Excerpt(output);

        Assert.StartsWith("...", excerpt, StringComparison.Ordinal);
        Assert.EndsWith("the actual error", excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", excerpt, StringComparison.Ordinal);
    }

    /// <summary>
    /// ssh's own words for never having connected, as each client measured says them - a name that did not
    /// resolve, an address where nothing answered, one that refused - read out of whatever else it printed.
    /// What it fails over after the host answered is not among them: there the host was reached.
    /// </summary>
    [Theory]
    [InlineData("ssh: Could not resolve hostname mac.local: No such host is known. \r\n", "ssh: Could not resolve hostname mac.local: No such host is known.")]
    [InlineData("ssh: Could not resolve hostname nosuchhost.invalid: Name or service not known\n", "ssh: Could not resolve hostname nosuchhost.invalid: Name or service not known")]
    [InlineData("ssh: connect to host 192.0.2.10 port 22: Connection timed out\n", "ssh: connect to host 192.0.2.10 port 22: Connection timed out")]
    [InlineData("ssh: connect to host 127.0.0.1 port 1: Connection refused\n", "ssh: connect to host 127.0.0.1 port 1: Connection refused")]
    [InlineData("banner exchange: Connection to UNKNOWN port -1: Connection refused\r\n", "banner exchange: Connection to UNKNOWN port -1: Connection refused")]
    [InlineData("Pseudo-terminal will not be allocated because stdin is not a terminal.\nssh: connect to host fe80::1%12 port 22: Network is unreachable\n", "ssh: connect to host fe80::1%12 port 22: Network is unreachable")]
    [InlineData("Connection to vps.example closed by remote host.\n", null)]
    [InlineData("Host key verification failed.\n", null)]
    [InlineData("harness@vps.example: Permission denied (publickey).\n", null)]
    [InlineData("kex_exchange_identification: read: Connection reset by peer\n", null)]
    [InlineData("banner exchange: Connection to 192.0.2.10 port 22: invalid format\n", null)]
    [InlineData("Timeout, server vps.example not responding.\n", null)]
    [InlineData("the build said: ssh: connect to host 192.0.2.10 port 22: Connection refused\n", null)]
    [InlineData("", null)]
    public void NeverConnected_ReadsSshsOwnWordsForNeverHavingConnected_AndNothingElse(string standardError, string? line)
    {
        Assert.Equal(line, HostProbes.NeverConnected(standardError));
    }

    /// <summary>
    /// Only ssh failing to connect reads as a host that could not be reached: not the same words from a
    /// program that ran and exited otherwise, from one that ran out of time, from WSL, whose programs keep
    /// their own exit codes, or from a program this machine ran itself.
    /// </summary>
    [Theory]
    [InlineData("ssh", 255, false, true)]
    [InlineData("ssh", 1, false, false)]
    [InlineData("ssh", 255, true, false)]
    [InlineData("wsl", 255, false, false)]
    [InlineData("wsl", -1, false, false)]
    [InlineData(null, 255, false, false)]
    public void Unreached_IsSshFailingToConnect_AndNothingElse(string? transport, int exitCode, bool timedOut, bool unreached)
    {
        var result = new ProcessResult(exitCode, string.Empty, "ssh: Could not resolve hostname mac.local: No such host is known.\n", TimeSpan.Zero, timedOut);
        var through = transport switch
        {
            "ssh" => new HostConnection { Host = HostId.Ssh("mac") },
            "wsl" => new HostConnection { Host = HostId.Wsl("Example-Linux"), Distribution = "Example-Linux" },
            _ => null,
        };

        var said = HostProbes.Unreached(result, through);

        Assert.Equal(unreached ? "the host could not be reached: ssh said ssh: Could not resolve hostname mac.local: No such host is known." : null, said);
    }

    /// <summary>
    /// A program whose end never came back is said as a host that could not be reached where ssh never
    /// connected, and otherwise as a program that may have run only in part, with what the connection said.
    /// </summary>
    [Theory]
    [InlineData("ssh: Could not resolve hostname mac.local: No such host is known.\n", "the host could not be reached: ssh said ssh: Could not resolve hostname mac.local: No such host is known.")]
    [InlineData("Connection to mac.local closed by remote host.\n", "'build' never reported how it finished, so it may not have run, or run only in part; the connection ended with exit 255: Connection to mac.local closed by remote host.")]
    [InlineData("", "'build' never reported how it finished, so it may not have run, or run only in part; the connection ended with exit 255")]
    public void NeverFinished_SaysTheHostWasNotReached_OnlyWhereSshNeverConnected(string standardError, string expected)
    {
        var said = HostProbes.NeverFinished("'build'", HostResults.Failed(255, standardError), new HostConnection { Host = HostId.Ssh("mac") });

        Assert.Equal(expected, said);
    }
}
