using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>What the DssHarness on a host answers, what it agrees to run, and how it says a run ended.</summary>
public sealed class HostAgentServiceTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static readonly Func<string, string[], CancellationToken, Task<int>> NothingRuns
        = (_, _, _) => throw new InvalidOperationException("Nothing should have run.");

    [Fact]
    public async Task Info_AnswersWithThisBuild_AndThisMachine()
    {
        using var output = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader("""{"kind":"info"}"""),
            output,
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(output.ToString(), HostAgentProtocol.JsonOptions);
        Assert.NotNull(info);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal("abc123", info.AssemblySha256);
        Assert.Equal("linux", info.Os);
        Assert.Equal("arm64", info.Processor);
    }

    [Fact]
    public async Task Run_RunsTheCommand_InTheHostsCopy_WithItsArgumentsUnchanged_AndSaysHowItFinished()
    {
        using var copy = new TempDirectory();
        using var error = new StringWriter();
        string? ranIn = null;
        string[]? ranWith = null;

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(copy.Path, "read-anchor", "D-A B", "--json")),
            new StringWriter(),
            error,
            (directory, arguments, _) =>
            {
                ranIn = directory;
                ranWith = arguments;
                return Task.FromResult(4);
            },
            TestContext.Current.CancellationToken);

        // The command's own exit code is what the host reports, unchanged.
        Assert.Equal(4, exitCode);
        Assert.Equal(copy.Path, ranIn);
        Assert.NotNull(ranWith);
        Assert.Equal(["read-anchor", "D-A B", "--json"], ranWith);

        // And the last line says so, where the machine that asked reads it instead of from ssh's exit code.
        Assert.Equal(HostAgentProtocol.CompletionLine(Nonce, 4), error.ToString().TrimEnd());
    }

    [Fact]
    public async Task Run_FindsACopyNamedFromTheHomeDirectory()
    {
        using var home = new TempDirectory();
        Directory.CreateDirectory(home.Combine("src", "repo"));
        string? ranIn = null;

        var exitCode = await Service(home.Path).ServeAsync(
            new StringReader(RunRequest("~/src/repo", "verify-git")),
            new StringWriter(),
            new StringWriter(),
            (directory, _, _) =>
            {
                ranIn = directory;
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);
        Assert.Equal(Path.GetFullPath(home.Combine("src", "repo")), ranIn);
    }

    [Fact]
    public async Task Run_ReportsAHostWithNoCopyOfTheRepository_AsUnavailable_InItsCompletionLineToo()
    {
        using var temp = new TempDirectory();
        using var error = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(temp.Combine("absent"), "verify-git")),
            new StringWriter(),
            error,
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, exitCode);
        Assert.Contains("no copy of the repository", error.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(
            HostAgentProtocol.CompletionLine(Nonce, HarnessExit.HostUnavailable),
            error.ToString().TrimEnd(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ReportsACopyThatCannotBeEntered_AsUnavailable()
    {
        using var copy = new TempDirectory();
        using var error = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(copy.Path, "verify-git")),
            new StringWriter(),
            error,
            (_, _, _) => throw new UnauthorizedAccessException("Access to the path is denied."),
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, exitCode);
        Assert.Contains("could not be entered: Access to the path is denied.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_CancelsTheCommand_WhenTheInputEnds_BecauseTheMachineThatAskedHasGone()
    {
        using var copy = new TempDirectory();
        using var input = new HeldOpenReader(RunRequest(copy.Path, "verify-git"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service().ServeAsync(
            input,
            new StringWriter(),
            new StringWriter(),
            async (_, _, token) =>
            {
                started.SetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return HarnessExit.Success;
                }
                catch (OperationCanceledException)
                {
                    return HarnessExit.Cancelled;
                }
            },
            cancellationToken);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        input.End();

        Assert.Equal(HarnessExit.Cancelled, await serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
    }

    [Fact]
    public async Task Run_LeavesTheCommandRunning_WhileTheInputStaysOpen()
    {
        using var copy = new TempDirectory();
        using var input = new HeldOpenReader(RunRequest(copy.Path, "verify-git"));
        var cancellationToken = TestContext.Current.CancellationToken;
        bool? cancelled = null;

        var exitCode = await Service().ServeAsync(
            input,
            new StringWriter(),
            new StringWriter(),
            async (_, _, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                cancelled = token.IsCancellationRequested;
                return HarnessExit.Success;
            },
            cancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);
        Assert.False(cancelled);
    }

    [Theory]
    [InlineData(HostExecService.CommandName)]
    [InlineData(HostAgentProtocol.CommandName)]
    public async Task Run_RefusesToPassTheWorkOnToAnotherHost(string command)
    {
        using var temp = new TempDirectory();

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(temp.Path, command, "--ssh", "other", "--", "verify-git")),
            new StringWriter(),
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, exitCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"kind":"info","protocol":99}""")]
    [InlineData("""{"kind":"run","arguments":[]}""")]
    [InlineData("""{"kind":"run","directory":"/r","arguments":["verify-git"]}""")]
    [InlineData("""{"kind":"info","unexpected":true}""")]
    [InlineData("""{"kind":"info","emulators":{"qemu":{"hostOs":"linux","hostProcessor":"x86_64","processor":"arm64","witness":{"command":["w"],"pattern":"x"}},"QEMU":{}}}""")]
    public async Task ARequestThatCannotBeServed_IsAUsageError_Explained(string request)
    {
        using var error = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader(request),
            new StringWriter(),
            error,
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, exitCode);
        Assert.StartsWith("host-agent: FAIL - ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestInAnotherProtocol_IsRefusedAsThat_ThoughItHasFieldsThisBuildDoesNotKnow()
    {
        using var error = new StringWriter();

        var other = HostAgentProtocol.Version + 1;

        var exitCode = await Service().ServeAsync(
            new StringReader($$"""{"kind":"info","protocol":{{other}},"addedLater":true}"""),
            new StringWriter(),
            error,
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, exitCode);
        Assert.Contains(
            $"speaks protocol {other}, and DssHarness 1.2.3 on this host speaks {HostAgentProtocol.Version}",
            error.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Asked about a developer environment, a host says whether it can set it up there, under the name
    /// it was asked by, compared ignoring case; this one runs Linux, where Visual Studio never does.
    /// </summary>
    [Fact]
    public async Task Info_SaysWhichDeveloperEnvironmentsCanBeSetUpHere()
    {
        using var output = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader("""{"kind":"info","developerEnvironments":{"VS":{"kind":"visualStudio"}}}"""),
            output,
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(output.ToString(), HostAgentProtocol.JsonOptions);
        Assert.NotNull(info);

        var check = info.DeveloperEnvironments["vs"];
        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal("Visual Studio is set up on windows, and this host runs linux", check.Reason);
    }

    [Fact]
    public void ARequestAndAnAnswer_ReadBack_CompareEmulatorNamesIgnoringCase()
    {
        // Emulator names are names a person typed in config.json, where case is ignored. Read back on
        // either side of a host, they must compare the same way, not by the serializer's default comparer.
        var request = JsonSerializer.Deserialize<HostAgentRequest>(
            """{"kind":"info","emulators":{"Qemu-Arm64":{"hostOs":"linux","hostProcessor":"x86_64","processor":"arm64","witness":{"command":["uname","-m"],"pattern":"^aarch64$"}}}}""",
            HostAgentProtocol.JsonOptions);
        var info = JsonSerializer.Deserialize<HostAgentInfo>(
            """{"version":"1.2.3","assemblySha256":"abc123","os":"linux","processor":"x86_64","emulators":{"Qemu-Arm64":{"available":true,"witnessed":"aarch64"}}}""",
            HostAgentProtocol.JsonOptions);

        Assert.NotNull(request);
        Assert.NotNull(info);
        Assert.True(request.Emulators.ContainsKey("qemu-arm64"));
        Assert.True(info.Emulators.ContainsKey("qemu-arm64"));
    }

    private static string RunRequest(string directory, params string[] arguments)
        => JsonSerializer.Serialize(
            new HostAgentRequest { Kind = HostAgentRequestKind.Run, Directory = directory, Arguments = [.. arguments], Nonce = Nonce },
            HostAgentProtocol.JsonOptions);

    private static HostAgentService Service(string? home = null)
    {
        var platform = HostDoubles.Platform(PlatformId.Linux, "arm64", home);

        var identity = Substitute.For<IToolIdentityProvider>();
        identity.Current.Returns(new ToolIdentity("1.2.3", "abc123"));

        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var processRunner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create());

        return new HostAgentService(
            platform,
            identity,
            new EmulatorProbe(platform, processRunner, fileSystem),
            new DeveloperEnvironmentProbe(platform, processRunner),
            fileSystem,
            new LocalProgramResolver(platform, FilePermissionsFactory.Create()));
    }
}
