using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>Which hosts .harness-config/ssh/config declares, and which keys it gives them.</summary>
public sealed class SshConfigFileTests
{
    [Fact]
    public void Find_ReturnsTheEntryThatNamesTheHost_WithItsKeys()
    {
        const string Text = """
            # hosts the harness reaches
            Host build-box
              HostName 10.0.0.5
              IdentityFile ~/.ssh/build_ed25519

            Host vps mirror
              HostName vps.example
              User dev
              IdentityFile .harness-config/ssh/vps_ed25519
              IdentityFile "keys/with space"
            """;

        var entry = SshConfigFile.Find(Text, "vps");

        Assert.NotNull(entry);
        Assert.Equal([".harness-config/ssh/vps_ed25519", "keys/with space"], entry.IdentityFiles);
    }

    [Fact]
    public void Find_ReturnsNull_WhenOnlyAWildcardWouldMatch()
    {
        // A misspelt name in config.json must not connect to whatever machine a pattern happens to match.
        Assert.Null(SshConfigFile.Find("Host *\n  User dev\n", "vps"));
        Assert.Null(SshConfigFile.Find("Host vps*\n", "vps"));
    }

    [Fact]
    public void Find_MatchesNamesCaseIncluded_AsSshDoes()
    {
        // ssh applies Host VPS to "ssh VPS" and never to "ssh vps", so the file does not declare vps.
        Assert.Null(SshConfigFile.Find("Host VPS\n  IdentityFile /upper/key\n", "vps"));

        var entry = SshConfigFile.Find("Host vps\n  IdentityFile /vps/key\n\nHost V*\n  IdentityFile /upper/key\n", "vps");

        Assert.NotNull(entry);
        Assert.Equal(["/vps/key"], entry.IdentityFiles);
    }

    [Fact]
    public void Find_AcceptsTheEqualsForm_AndGathersKeysAsSshApplies()
    {
        // A key before the first Host line applies to every host, as ssh applies it. A Match block's key is
        // left out, since its conditions are not evaluated here.
        var entry = SshConfigFile.Find(
            "IdentityFile /global/key\nHost=vps\nIdentityFile=/vps/key\nMatch host other\nIdentityFile /other/key\n",
            "vps");

        Assert.NotNull(entry);
        Assert.Equal(["/global/key", "/vps/key"], entry.IdentityFiles);
    }

    [Fact]
    public void Find_GathersTheKeysOfEveryEntryWhosePatternsSelectTheHost()
    {
        const string Text = """
            Host vps
              IdentityFile /vps/key

            Host v?s build-*
              IdentityFile /single-character/key

            Host * !vps
              IdentityFile /everyone-else/key

            Host other
              IdentityFile /other/key

            Host *
              IdentityFile ~/.ssh/shared
            """;

        var entry = SshConfigFile.Find(Text, "vps");

        Assert.NotNull(entry);
        Assert.Equal(["/vps/key", "/single-character/key", "~/.ssh/shared"], entry.IdentityFiles);
    }

    [Fact]
    public void Find_ReadsAFileWithWindowsLineEndings()
    {
        var entry = SshConfigFile.Find("Host vps\r\n  IdentityFile C:/keys/vps\r\n", "vps");

        Assert.NotNull(entry);
        Assert.Equal(["C:/keys/vps"], entry.IdentityFiles);
    }
}
