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

    [Fact]
    public async Task OnACmdShell_NoDirectoriesAreSearched_AndWhereIsAsked()
    {
        var asked = new List<string>();

        var commands = new ScriptedHostCommands((_, command) =>
        {
            asked.Add(command.Program);
            return HostResults.Failed(1, "INFO: Could not find files");
        });

        var connection = await Resolve(commands, SshHost with { Shell = RemoteShell.Cmd }, ["dotnet"]);

        Assert.Equal(ProgramFound.Nowhere, connection.Located("dotnet")?.Found);
        Assert.Equal(["where"], asked);
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

        Assert.Equal(ProgramFound.Nowhere, connection.Located("dotnet")?.Found);
        Assert.DoesNotContain(candidates, path => path.Contains(' ', StringComparison.Ordinal));
        Assert.Contains("/usr/local/bin/dotnet", candidates);
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

    private static Task<HostConnection> Resolve(ScriptedHostCommands commands, HostConnection connection, string[] wanted)
        => new HostProgramResolver(
                new LocalProgramResolver(HostDoubles.Platform(), Substitute.For<IFilePermissions>()),
                commands)
            .ResolveAsync(
                connection,
                wanted,
                ToolSearchDirectories.Posix,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
}
