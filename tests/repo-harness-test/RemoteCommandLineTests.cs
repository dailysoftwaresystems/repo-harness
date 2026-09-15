using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>
/// The line an ssh server hands its shell is never quoted, so what it may hold is the whole defence:
/// a word any of sh, cmd or PowerShell would reinterpret must be refused before it is sent.
/// </summary>
public sealed class RemoteCommandLineTests
{
    [Fact]
    public void Join_PassesWords_EveryShellReadsLiterally()
    {
        Assert.Equal(
            "dotnet tool update --global DssHarness --version 1.2.3-beta+abc.1",
            RemoteCommandLine.Join(["dotnet", "tool", "update", "--global", "DssHarness", "--version", "1.2.3-beta+abc.1"], RemoteShell.Standard));

        Assert.Equal(
            ".dotnet/tools/DssHarness host-agent",
            RemoteCommandLine.Join([".dotnet/tools/DssHarness", "host-agent"], RemoteShell.Standard));

        Assert.Equal("ps -A -o comm=", RemoteCommandLine.Join(["ps", "-A", "-o", "comm="], RemoteShell.Standard));
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("quote\"inside")]
    [InlineData("it's")]
    [InlineData("$HOME")]
    [InlineData("%PATH%")]
    [InlineData("a;b")]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("`id`")]
    [InlineData("a\\b")]
    [InlineData("~/repo")]
    [InlineData("@arguments")]
    [InlineData("a,b")]
    [InlineData("a*")]
    [InlineData("")]
    public void Join_RefusesAWord_AShellCouldReinterpret(string word)
    {
        Assert.Throws<ArgumentException>(() => RemoteCommandLine.Join(["DssHarness", word], RemoteShell.Standard));
    }

    [Fact]
    public void Join_AcceptsABackslash_OnlyForCmd_WhichNeedsItInAPath()
    {
        Assert.Equal(
            @".dotnet\tools\DssHarness.exe host-agent",
            RemoteCommandLine.Join([@".dotnet\tools\DssHarness.exe", "host-agent"], RemoteShell.Cmd));

        Assert.Throws<ArgumentException>(() => RemoteCommandLine.Join([@".dotnet\tools\DssHarness.exe"], RemoteShell.Standard));
    }

    [Theory]
    [InlineData("C:\\WINDOWS\\system32\\cmd.exe\r\n", RemoteShell.Cmd)]
    [InlineData("%COMSPEC%\n", RemoteShell.Standard)]
    public void ReadShellProbe_TellsCmd_FromEveryOtherShell(string output, RemoteShell expected)
    {
        Assert.Equal(expected, RemoteCommandLine.ReadShellProbe(output));
    }
}
