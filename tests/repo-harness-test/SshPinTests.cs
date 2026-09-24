using RepoHarness.Core.Hosts;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// What ssh says it would do, read, and the pin made from it: ssh given the address this machine resolved,
/// and changed in nothing else it does.
/// </summary>
public sealed class SshPinTests
{
    /// <summary>
    /// ssh -G is read line by line, as the clients measured print it: the name it would look up, and a jump
    /// host, a command or an alias of its configuration's own, where it sets one - "none" is ssh's word for
    /// none. Where ssh failed, ran out of time, or named no hostname, it said nothing that can be read.
    /// </summary>
    [Theory]
    [InlineData("user u\r\nhostname plain.test\r\nport 22\r\n", "plain.test", false, null)]
    [InlineData("hostname proxied.test\nproxyjump bastion.test\n", "proxied.test", true, null)]
    [InlineData("hostname commanded.test\nproxycommand nc %h %p\n", "commanded.test", true, null)]
    [InlineData("hostname plain.test\nproxyjump none\nproxycommand none\n", "plain.test", false, null)]
    [InlineData("hostname aliased.test\nhostkeyalias fixed-alias\n", "aliased.test", false, "fixed-alias")]
    public void WhatSshSaysItWouldDo_IsRead(string printed, string hostName, bool proxied, string? alias)
    {
        var seen = SshSettings.Read(new ProcessResult(0, printed, string.Empty, TimeSpan.Zero, TimedOut: false));

        Assert.NotNull(seen);
        Assert.Equal(hostName, seen.HostName);
        Assert.Equal(proxied, seen.Proxied);
        Assert.Equal(alias, seen.HostKeyAlias);
    }

    [Theory]
    [InlineData(255, "hostname plain.test\n", false)]
    [InlineData(0, "hostname plain.test\n", true)]
    [InlineData(0, "user u\nport 22\n", false)]
    [InlineData(0, "hostname\n", false)]
    [InlineData(0, "", false)]
    public void WhatSshCouldNotSay_ReadsAsNothing(int exitCode, string printed, bool timedOut)
    {
        Assert.Null(SshSettings.Read(new ProcessResult(exitCode, printed, string.Empty, TimeSpan.Zero, timedOut)));
    }

    /// <summary>
    /// Two answers differ only in where the connection goes where nothing but the name or address, the key's
    /// alias and the address check moved: a Match block keyed by the host, turning anything else on or off,
    /// is a difference - as is a setting one has and the other lacks.
    /// </summary>
    [Theory]
    [InlineData("hostname 192.0.2.10\nhostkeyalias [plain.test]:2222\ncheckhostip no\nforwardagent no\n", true)]
    [InlineData("hostname 192.0.2.10\nhostkeyalias [plain.test]:2222\ncheckhostip no\nforwardagent yes\n", false)]
    [InlineData("hostname 192.0.2.10\nhostkeyalias [plain.test]:2222\ncheckhostip no\nforwardagent no\nidentityfile ~/.ssh/other\n", false)]
    public void Answers_DifferOnlyInWhereTheyGo_WhereNothingElseMoved(string pinned, bool same)
    {
        var unpinned = Read("hostname plain.test\ncheckhostip yes\nforwardagent no\n");

        Assert.Equal(same, unpinned.DifferOnlyInWhereTheyGo(Read(pinned)));
    }

    /// <summary>
    /// A pin keys the host as ssh would unpinned: its configuration's own alias, or the name it dials, off port
    /// 22 as known_hosts spells such a host. An address ssh would dial anyway is no pin, whatever its case.
    /// </summary>
    [Theory]
    [InlineData("hostname plain.test\n", "192.0.2.10", 22, "plain.test")]
    [InlineData("hostname plain.test\n", "192.0.2.10", 2222, "[plain.test]:2222")]
    [InlineData("hostname plain.test\nhostkeyalias fixed-alias\n", "192.0.2.10", 2222, "fixed-alias")]
    [InlineData("hostname 192.0.2.10\n", "192.0.2.10", 2222, null)]
    [InlineData("hostname fe80::1%12\n", "FE80::1%12", 22, null)]
    public void APin_KeysTheHostAsSshWouldUnpinned(string printed, string address, int port, string? alias)
    {
        var pin = SshPin.For(Read(printed), address, port);

        Assert.Equal(alias, pin?.KeyAlias);
        Assert.Equal(alias is null ? null : address, pin?.Address);
    }

    /// <summary>A pin holds until it is dropped, and stays dropped, for every copy of the connection it was made for.</summary>
    [Fact]
    public void APin_HoldsUntilDropped_ForEveryCopyOfItsConnection()
    {
        var pin = new SshPin("192.0.2.10", "plain.test");
        var connection = new HostConnection { Host = HostId.Ssh("plain"), Pin = pin };
        var copy = connection with { Shell = RemoteShell.Cmd };

        Assert.True(copy.Pin!.Holds);

        connection.Pin!.Drop();
        pin.Drop();

        Assert.False(copy.Pin.Holds);
    }

    private static SshSettings Read(string printed)
        => SshSettings.Read(new ProcessResult(0, printed, string.Empty, TimeSpan.Zero, TimedOut: false))
            ?? throw new InvalidOperationException("ssh said nothing readable.");
}
