using RepoHarness.Core.Hosts;

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
            {"version":1,"data":[{"packageId":"dotnet-dump","version":"9.0.1","commands":["dotnet-dump"]},{"packageId":"repoharness","version":"0.2.0-beta","commands":["repo-harness"]}]}
            """;

        Assert.True(HostProbes.TryReadToolVersion(Output, "RepoHarness", out var version));
        Assert.Equal("0.2.0-beta", version);
    }

    [Fact]
    public void TryReadToolVersion_ReadsATool_ThatIsNotInstalled()
    {
        Assert.True(HostProbes.TryReadToolVersion("""{"version":1,"data":[]}""", "RepoHarness", out var version));
        Assert.Null(version);
    }

    [Fact]
    public void TryReadToolVersion_FindsTheDocument_AfterABanner()
    {
        const string Output = "Welcome to .NET!\n{\"version\":1,\"data\":[{\"packageId\":\"repoharness\",\"version\":\"1.0.0\"}]}";

        Assert.True(HostProbes.TryReadToolVersion(Output, "RepoHarness", out var version));
        Assert.Equal("1.0.0", version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tool list failed")]
    [InlineData("{ not json")]
    [InlineData("""{"version":1}""")]
    public void TryReadToolVersion_RefusesTextThatIsNotTheListing(string output)
    {
        Assert.False(HostProbes.TryReadToolVersion(output, "RepoHarness", out _));
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
    [InlineData("bash\nsshd\nrepo-harness\n", true)]
    [InlineData("/Users/dev/.dotnet/tools/repo-harness\n", true)]
    [InlineData("\"svchost.exe\",\"1234\",\"Services\",\"0\",\"10,000 K\"\r\n\"repo-harness.exe\",\"4321\",\"Console\",\"1\",\"50,000 K\"\r\n", true)]
    [InlineData("bash\nrepo-harness-helper\nsshd\n", false)]
    [InlineData("\"svchost.exe\",\"1234\",\"Services\",\"0\",\"10,000 K\"\r\n", false)]
    public void ListsProcess_FindsTheTool_InEitherKindOfListing(string listing, bool expected)
    {
        Assert.Equal(expected, HostProbes.ListsProcess(listing, "repo-harness"));
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
}
