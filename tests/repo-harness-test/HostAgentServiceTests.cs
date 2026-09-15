using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>What the repo-harness on a host answers, and what it agrees to run.</summary>
public sealed class HostAgentServiceTests
{
    private static readonly Func<string, string[], Task<int>> NothingRuns
        = (_, _) => throw new InvalidOperationException("Nothing should have run.");

    [Fact]
    public async Task Info_AnswersWithThisBuild_AndThisMachine()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader("""{"kind":"info"}"""),
            output,
            error,
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
    public async Task Run_RunsTheCommand_InTheHostsCopy_WithItsArgumentsUnchanged()
    {
        using var copy = new TempDirectory();
        string? ranIn = null;
        string[]? ranWith = null;

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(copy.Path, "read-anchor", "D-A B", "--json")),
            new StringWriter(),
            new StringWriter(),
            (directory, arguments) =>
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
            (directory, _) =>
            {
                ranIn = directory;
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);
        Assert.Equal(Path.GetFullPath(home.Combine("src", "repo")), ranIn);
    }

    [Fact]
    public async Task Run_ReportsAHostWithNoCopyOfTheRepository_AsUnavailable()
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
    [InlineData("""{"kind":"info","protocol":99}""")]
    [InlineData("""{"kind":"run","arguments":[]}""")]
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
            new HostAgentRequest { Kind = HostAgentRequestKind.Run, Directory = directory, Arguments = [.. arguments] },
            HostAgentProtocol.JsonOptions);

    private static HostAgentService Service(string? home = null)
    {
        var platform = Substitute.For<IHostPlatform>();
        platform.PlatformKey.Returns("linux");
        platform.Processor.Returns("arm64");
        platform.HomeDirectory.Returns(home ?? TestHost.TemporaryRoot);

        var identity = Substitute.For<IToolIdentityProvider>();
        identity.Current.Returns(new ToolIdentity("1.2.3", "abc123"));

        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var processRunner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create());

        return new HostAgentService(platform, identity, new EmulatorProbe(platform, processRunner, fileSystem), fileSystem);
    }
}
