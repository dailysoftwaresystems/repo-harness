using NSubstitute;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// A login shell's PATH is not the PATH a command sees. Measured: /opt/homebrew/bin is absent from the
/// PATH of a command run over ssh on macOS, and ~/.dotnet is absent from the PATH of a command run with
/// wsl.exe --exec, which is where the .NET install script puts the SDK. Each leg then fails saying the
/// SDK is not installed, when it is.
/// </summary>
public sealed class HostProgramResolverTests
{
    private const string Home = "/home/harness";

    private static readonly HostConnection SshHost = new()
    {
        Host = HostId.Ssh("build-box"),
        Address = "host.invalid",
        User = "harness",
        KeyFile = "/repo/.key",
        KnownHostsFile = "/repo/known_hosts",
    };

    [Fact]
    public async Task AProgramThePathNames_IsStartedByItsName()
    {
        var commands = new ScriptedHostCommands((_, command) => (command.Program, command.Arguments.FirstOrDefault()) switch
        {
            ("command", "-v") => HostResults.Ok("/usr/bin/dotnet\n"),
            _ => throw HostResults.Unexpected(command),
        });

        var connection = await Resolve(commands, SshHost, ["dotnet"]);

        Assert.Equal(ProgramFound.OnPath, connection.Located("dotnet")?.Found);
        Assert.Equal("dotnet", connection.Spell("dotnet"));
    }

    [Fact]
    public async Task AProgramOffThePath_IsFoundWhereAnInstallerPutIt_AndStartedByItsAbsolutePath()
    {
        var listed = new List<IReadOnlyList<string>>();

        var commands = new ScriptedHostCommands((_, command) => (command.Program, command.Arguments.FirstOrDefault()) switch
        {
            ("command", "-v") => HostResults.Failed(1, string.Empty),
            ("where", _) => HostResults.Failed(1, "not found"),
            ("pwd", _) => HostResults.Ok(Home + "\n"),
            ("ls", _) => Listed(command, listed),
            _ => throw HostResults.Unexpected(command),
        });

        var connection = await Resolve(commands, SshHost, ["dotnet"]);

        Assert.Equal(ProgramFound.OffPath, connection.Located("dotnet")?.Found);
        Assert.Equal($"{Home}/.dotnet/dotnet", connection.Spell("dotnet"));

        // Every candidate is listed by one command: a separate one for each would open a separate ssh
        // connection for each, and a host that is merely far away would take a minute to answer.
        var candidates = Assert.Single(listed);
        Assert.Contains($"{Home}/.dotnet/dotnet", candidates);
        Assert.Contains("/opt/homebrew/bin/dotnet", candidates);
    }

    [Fact]
    public async Task AProgramNothingHas_IsReportedAsNowhere_RatherThanAsUnknown()
    {
        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "command" or "where" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Ok(Home + "\n"),
            "ls" => HostResults.Failed(2, "ls: No such file or directory"),
            _ => throw HostResults.Unexpected(command),
        });

        var connection = await Resolve(commands, SshHost, ["ninja"]);

        Assert.Equal(ProgramFound.Nowhere, connection.Located("ninja")?.Found);
    }

    [Fact]
    public async Task AHostThatDidNotAnswer_IsReportedAsUnreadable_SoNoRefusalClaimsTheToolIsMissing()
    {
        // ssh's own 255 means the connection never opened, which says nothing about what is installed.
        var commands = new ScriptedHostCommands((_, _) => HostResults.Failed(255, "Connection closed by remote host"));

        var connection = await Resolve(commands, SshHost, ["dotnet"]);

        Assert.Equal(ProgramFound.Unreadable, connection.Located("dotnet")?.Found);
    }

    [Fact]
    public async Task InAWslDistribution_TheLookupIsRunThroughAShell_BecauseExecStartsNoShell()
    {
        var asked = new List<string>();

        var commands = new ScriptedHostCommands((_, command) =>
        {
            asked.Add($"{command.Program} {string.Join(' ', command.Arguments)}");

            return command.Program == "sh" ? HostResults.Ok("/usr/bin/dotnet\n") : throw HostResults.Unexpected(command);
        });

        var connection = await Resolve(
            commands,
            new HostConnection { Host = HostId.Wsl("lane-a"), Distribution = "Example-Linux" },
            ["dotnet"]);

        Assert.Equal(ProgramFound.OnPath, connection.Located("dotnet")?.Found);
        Assert.Equal(["sh -c command -v dotnet"], asked);
    }

    /// <summary>
    /// cmd has no portable way to test one path, so a host told to look in directories cannot, and a
    /// program its PATH does not name is not known either way: calling it missing would have
    /// install-missing-tools install a second copy of one a leg there can already start.
    /// </summary>
    [Fact]
    public async Task OnACmdShell_TheDirectoriesCannotBeLookedIn_SoAProgramOffThePathIsUnknown()
    {
        var asked = new List<string>();

        var commands = new ScriptedHostCommands((_, command) =>
        {
            asked.Add(command.Program);
            return HostResults.Failed(1, "INFO: Could not find files");
        });

        var connection = await Resolve(commands, SshHost with { Shell = RemoteShell.Cmd }, ["cmake"], [@"C:\tools"]);
        var located = connection.Located("cmake");

        Assert.Equal(ProgramFound.Unreadable, located?.Found);
        Assert.Equal(
            "'cmake' is not on the PATH there, and its shell, cmd, gives this no way to look in the directories searched for programs",
            located?.Reason);
        Assert.Equal(["where"], asked);
    }

    /// <summary>With nothing to look in beyond the PATH, the PATH was everywhere there was to look.</summary>
    [Fact]
    public async Task OnACmdShell_WithNoDirectoriesToLookIn_AProgramOffThePathIsNowhere()
    {
        var commands = new ScriptedHostCommands((_, _) => HostResults.Failed(1, "INFO: Could not find files"));

        var connection = await Resolve(commands, SshHost with { Shell = RemoteShell.Cmd }, ["dotnet"], []);

        Assert.Equal(ProgramFound.Nowhere, connection.Located("dotnet")?.Found);
    }

    /// <summary>
    /// A POSIX 'ls' given files prints the ones that exist, as given, and nothing else. PowerShell -
    /// which no probe tells apart from a POSIX shell - prints a table, and reading that as "none of
    /// them exist" would call every program there missing.
    /// </summary>
    [Fact]
    public async Task AListingThatIsNotPosixs_LeavesTheProgramUnknown_NotMissing()
    {
        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "command" or "where" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Ok(Home + "\n"),
            "ls" => HostResults.Ok("\n    Directory: /usr/local/bin\n\nMode   LastWriteTime   Length Name\n----   -------------   ------ ----\n-a---  1/1/2026 0:00  1024   dotnet\n"),
            _ => throw HostResults.Unexpected(command),
        });

        var located = (await Resolve(commands, SshHost, ["dotnet"])).Located("dotnet");

        Assert.Equal(ProgramFound.Unreadable, located?.Found);
        Assert.Contains("is not a listing this can read", located?.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no home directory to expand them against, the directories under it were not looked in,
    /// and a program not found in the others is not known either way.
    /// </summary>
    [Fact]
    public async Task AHomeThatCouldNotBeRead_LeavesAProgramFoundNowhereElseUnknown()
    {
        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "command" or "where" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Failed(1, "pwd: cannot read"),
            "ls" => HostResults.Failed(2, "ls: No such file or directory"),
            _ => throw HostResults.Unexpected(command),
        });

        var located = (await Resolve(commands, SshHost, ["dotnet"], ["~/.dotnet", "/usr/local/bin"])).Located("dotnet");

        Assert.Equal(ProgramFound.Unreadable, located?.Found);
        Assert.Equal(
            "'dotnet' is not on the PATH there, and '~/.dotnet' could not be looked in, because its home directory could not be read",
            located?.Reason);
    }

    /// <summary>
    /// A Windows directory given to a host whose shell is not cmd - PowerShell over ssh - cannot be
    /// listed with a POSIX 'ls', and is said to be that rather than searched and found empty.
    /// </summary>
    [Fact]
    public async Task AWindowsDirectoryGivenToAShellThatIsNotCmd_IsSaidToBeUnlooked()
    {
        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "command" or "where" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Ok(Home + "\n"),
            _ => throw HostResults.Unexpected(command),
        });

        var located = (await Resolve(commands, SshHost, ["cmake"], [@"C:\tools"])).Located("cmake");

        Assert.Equal(ProgramFound.Unreadable, located?.Found);
        Assert.Contains(@"'C:\tools' could not be looked in, because only a POSIX path can be listed there", located?.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHomeDirectoryHoldingASpace_IsPassedOver_AndTheOtherDirectoriesAreStillSearched()
    {
        // A command line an ssh server hands to a shell carries only words every shell reads literally,
        // so a path with a space cannot travel in one. Sending it anyway would have the shell split it
        // and list two directories that do not exist.
        IReadOnlyList<string> candidates = [];

        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "command" or "where" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Ok("/home/two words\n"),
            "ls" => Remember(command, out candidates),
            _ => throw HostResults.Unexpected(command),
        });

        var connection = await Resolve(commands, SshHost, ["dotnet"]);
        var located = connection.Located("dotnet");

        // Looked for everywhere else, and said to be unknown rather than missing: the directories
        // passed over are the ones it may well be in.
        Assert.Equal(ProgramFound.Unreadable, located?.Found);
        Assert.Contains(
            "'~/.dotnet', '~/.local/bin', '~/bin' could not be looked in, because a path holding a space cannot be named",
            located?.Reason,
            StringComparison.Ordinal);
        Assert.DoesNotContain(candidates, path => path.Contains(' ', StringComparison.Ordinal));
        Assert.Contains("/usr/local/bin/dotnet", candidates);
    }

    /// <summary>
    /// A POSIX 'ls' given a candidate that is a directory prints what is in it as well. The candidate
    /// it printed as given is found all the same: read as "some other shell's listing", every program
    /// beside such a directory would be unknown.
    /// </summary>
    [Fact]
    public async Task ACandidateTheListingPrinted_IsFound_WhateverElseItPrinted()
    {
        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "command" or "where" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Ok(Home + "\n"),
            "ls" => HostResults.Ok("/usr/local/bin/cmake\n\n/opt/homebrew/bin/cmake:\nbin\nshare\n"),
            _ => throw HostResults.Unexpected(command),
        });

        var located = (await Resolve(commands, SshHost, ["cmake"])).Located("cmake");

        Assert.Equal(new ProgramLocation("cmake", ProgramFound.OffPath, "/usr/local/bin/cmake"), located);
    }

    /// <summary>
    /// wsl.exe --exec hands each argument to the program as it is, with no shell to split it, so a
    /// home directory holding a space is looked in there - never passed over as it must be over ssh.
    /// </summary>
    [Fact]
    public async Task InAWslDistribution_AHomeDirectoryHoldingASpace_IsLookedIn()
    {
        const string home = "/home/two words";

        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "sh" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Ok(home + "\n"),
            "ls" => HostResults.Ok(string.Join('\n', command.Arguments.Where(path => path == home + "/.dotnet/dotnet")) + "\n"),
            _ => throw HostResults.Unexpected(command),
        });

        var located = (await Resolve(
            commands,
            new HostConnection { Host = HostId.Wsl("lane-a"), Distribution = "Example-Linux" },
            ["dotnet"])).Located("dotnet");

        Assert.Equal(new ProgramLocation("dotnet", ProgramFound.OffPath, home + "/.dotnet/dotnet"), located);
    }

    /// <summary>
    /// A name no command line sent there can carry is never looked for, and says that: "the host did
    /// not answer" would send somebody to a host that was never asked.
    /// </summary>
    [Fact]
    public async Task AProgramNoCommandLineCanCarry_IsUnknown_SayingWhy()
    {
        var commands = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command));

        var located = (await Resolve(commands, SshHost, ["my tool"])).Located("my tool");

        Assert.Equal(ProgramFound.Unreadable, located?.Found);
        Assert.Equal("'my tool' holds characters a command line sent there cannot carry, so it could not be looked for", located?.WhyUnestablished());
        Assert.Empty(commands.Calls);
    }

    /// <summary>
    /// A listing the host never answered - ssh's own 255 - looked nowhere, so a program not on the
    /// PATH is not known either way rather than missing.
    /// </summary>
    [Fact]
    public async Task AListingTheHostNeverAnswered_LeavesTheProgramUnknown_NotMissing()
    {
        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "command" or "where" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Ok(Home + "\n"),
            "ls" => HostResults.Failed(HostProbes.SshFailed, "Connection closed by remote host"),
            _ => throw HostResults.Unexpected(command),
        });

        var located = (await Resolve(commands, SshHost, ["cmake"])).Located("cmake");

        Assert.Equal(ProgramFound.Unreadable, located?.Found);
        Assert.Equal("the host did not answer when asked where 'cmake' is", located?.WhyUnestablished());
    }

    /// <summary>
    /// A home directory the host never said, because it did not answer, is no reason of the
    /// search's own: that is the host not answering, which running again may change - never "its
    /// home directory could not be read".
    /// </summary>
    [Fact]
    public async Task AHomeTheHostNeverAnsweredFor_LeavesTheProgramUnanswered_NotUnreadable()
    {
        var commands = new ScriptedHostCommands((_, command) => command.Program switch
        {
            "command" or "where" => HostResults.Failed(1, string.Empty),
            "pwd" => HostResults.Failed(HostProbes.SshFailed, "Connection closed by remote host"),
            _ => throw HostResults.Unexpected(command),
        });

        var located = (await Resolve(commands, SshHost, ["dotnet"], ["~/.dotnet", "/usr/local/bin"])).Located("dotnet");

        Assert.Equal(ProgramFound.Unreadable, located?.Found);
        Assert.Null(located?.Reason);
        Assert.Equal("the host did not answer when asked where 'dotnet' is", located?.WhyUnestablished());
    }

    [Fact]
    public async Task AProgramAlreadyMeasured_IsNotMeasuredAgain()
    {
        var commands = new ScriptedHostCommands((_, command) => command.Program == "command"
            ? HostResults.Ok("/usr/bin/git\n")
            : throw HostResults.Unexpected(command));

        var already = SshHost with
        {
            Programs = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
            {
                ["dotnet"] = new("dotnet", ProgramFound.OffPath, "/opt/dotnet/dotnet"),
            },
        };

        var connection = await Resolve(commands, already, ["dotnet", "git"]);

        Assert.Equal("/opt/dotnet/dotnet", connection.Spell("dotnet"));
        Assert.Equal(["git"], commands.Calls.Select(call => call.Command.Arguments[^1]));
    }

    [Fact]
    public async Task OnThisMachine_ThePathIsWhatAChildOfThisProcessWouldFind()
    {
        var permissions = Substitute.For<IFilePermissions>();
        permissions.IsExecutable(Arg.Any<string>())
            .Returns(call => Path.GetFileNameWithoutExtension(call.Arg<string>()) == "dotnet");

        var commands = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command));
        var resolver = new HostProgramResolver(
            new LocalProgramResolver(HostDoubles.Platform(), permissions, () => "/usr/bin"),
            commands);

        var connection = await resolver.ResolveAsync(
            new HostConnection { Host = HostId.Local },
            ["dotnet", "ninja"],
            [],
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProgramFound.OnPath, connection.Located("dotnet")?.Found);
        Assert.Equal(ProgramFound.Nowhere, connection.Located("ninja")?.Found);
        Assert.Empty(commands.Calls);
    }

    /// <summary>Records the candidate paths a listing was asked about, and answers that it has none of them.</summary>
    private static ProcessResult Remember(HostCommand command, out IReadOnlyList<string> candidates)
    {
        candidates = command.Arguments;
        return HostResults.Failed(2, "ls: No such file or directory");
    }

    /// <summary>Answers a listing of candidate paths with the ones a host would have, sorted as ls sorts them.</summary>
    private static ProcessResult Listed(HostCommand command, List<IReadOnlyList<string>> listed)
    {
        listed.Add(command.Arguments);

        var present = command.Arguments
            .Where(path => path.StartsWith(Home + "/.dotnet/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        return HostResults.Ok(string.Join('\n', present) + "\n");
    }

    private static Task<HostConnection> Resolve(
        ScriptedHostCommands commands,
        HostConnection connection,
        string[] wanted,
        IReadOnlyList<string>? directories = null)
        => new HostProgramResolver(
                new LocalProgramResolver(HostDoubles.Platform(), Substitute.For<IFilePermissions>()),
                commands)
            .ResolveAsync(
                connection,
                wanted,
                directories ?? ToolSearchDirectories.Posix,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
}
