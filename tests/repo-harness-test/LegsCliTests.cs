using System.Security.Cryptography;
using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The built CLI's legs, host-exec and host-agent commands, end to end. Only this machine is declared
/// as a host, so nothing here needs WSL, ssh or a network.
/// </summary>
public sealed class LegsCliTests
{
    [Fact]
    public async Task Legs_ShowsWhereEachLegRuns_AndWarnsAboutALegNoHostCanRun()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(["legs", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains("native", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("runs on local", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("leg 'elsewhere' cannot run", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legs_Fails_WhenALegNamedWithLegsCannotRun(bool separatedBySpace)
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        string[] names = separatedBySpace ? ["native", "elsewhere"] : ["native,elsewhere"];
        var result = await CliRunner.RunAsync(["legs", "--legs", .. names, "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(LegsExit.Unavailable, result.ExitCode);
        Assert.Contains("leg 'elsewhere' cannot run", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legs_RefusesALegsValueThatNamesNothing()
    {
        // An unset variable in --legs "$GATE" must not quietly check every leg under the rule for unnamed ones.
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(["legs", "--legs", "", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("--legs was given no leg or leg set name", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legs_RefusesAnUnknownName()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(["legs", "--legs", "nope", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("'nope'", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legs_WritesJsonAloneOnStandardOutput()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(["legs", "--json", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var legs = document.RootElement.GetProperty("legs");
        Assert.Equal(2, legs.GetArrayLength());
        Assert.Equal("local", legs[0].GetProperty("host").GetString());
        Assert.False(legs[1].GetProperty("runnable").GetBoolean());
    }

    [Fact]
    public async Task HostExec_WithoutAHost_IsAUsageError()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(["host-exec", "-C", temp.Path, "--", "verify-git"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task HostExec_ToAnUndeclaredSshHost_IsAUsageError()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(
            ["host-exec", "--ssh", "nowhere", "-C", temp.Path, "--", "verify-git"],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("--ssh nowhere is not declared under hosts.ssh", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostExec_ToWslsDefaultDistribution_OnAMachineThatIsNotWindows_IsUnavailable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows may have WSL; no other operating system does.");

        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(["host-exec", "--wsl", "-C", temp.Path, "--", "verify-git"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, result.ExitCode);
        Assert.Contains("WSL exists only on Windows", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostAgent_AnswersAsThisBuild_OfThisMachine()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = (await CliRunner.RunAsync(["--version"], cancellationToken)).TrimmedOutput;

        var result = await CliRunner.RunAsync(["host-agent"], cancellationToken, standardInput: """{"kind":"info"}""" + "\n");

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(result.StandardOutput, HostAgentProtocol.JsonOptions);
        var platform = new HostPlatform();

        Assert.NotNull(info);
        Assert.Equal(version, info.Version);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(CliRunner.CliAssemblyPath))), info.AssemblySha256);
        Assert.Equal(platform.PlatformKey, info.Os);
        Assert.Equal(platform.Processor, info.Processor);
    }

    [Fact]
    public async Task HostAgent_RunsTheCommand_InTheDirectoryItIsGiven()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var nonce = HostAgentProtocol.NewNonce();
        var request = JsonSerializer.Serialize(
            new HostAgentRequest { Kind = HostAgentRequestKind.Run, Directory = temp.Path, Arguments = ["verify-git"], Nonce = nonce },
            HostAgentProtocol.JsonOptions);

        var result = await CliRunner.RunAsync(
            [HostAgentProtocol.CommandName, HostAgentProtocol.VerboseOption],
            cancellationToken,
            standardInput: request + "\n");

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains("verify-git: OK", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(temp.Path, result.StandardOutput, StringComparison.OrdinalIgnoreCase);

        // The last line says how the command finished; the machine that asked reads its exit code from there.
        Assert.EndsWith(
            HostAgentProtocol.CompletionLine(nonce, HarnessExit.Success),
            result.StandardError.TrimEnd(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostAgent_ReportsAFailureNothingAnticipated_AsAnInternalError()
    {
        // config.json refuses a witness pattern that is not a regular expression before any request is sent,
        // so nothing on a host anticipates one. A request that carries one anyway ends in the handler for
        // defects, which must report it as every other command does, rather than as a stack trace and exit 1.
        var platform = new HostPlatform();
        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Info,
                Emulators = new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["broken"] = new EmulatorConfig
                    {
                        HostOs = platform.PlatformKey,
                        HostProcessor = platform.Processor,
                        Processor = "riscv64",
                        Launcher = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath],
                        Env = { [TestHost.ChildModeVariable] = "echo-args" },
                        Witness = new EmulatorWitness { Command = ["riscv64"], Pattern = "(" },
                    },
                },
            },
            HostAgentProtocol.JsonOptions);

        var result = await CliRunner.RunAsync([HostAgentProtocol.CommandName], TestContext.Current.CancellationToken, standardInput: request + "\n");

        Assert.Equal(HarnessExit.InternalError, result.ExitCode);
        Assert.StartsWith($"{HostAgentProtocol.CommandName}: FAIL - Unexpected ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostAgent_IsNotListedInHelp_BecauseNobodyTypesIt()
    {
        var result = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        Assert.DoesNotContain(HostAgentProtocol.CommandName, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolPackage_IsWhatTheCliProjectPublishes()
    {
        // A host is told to install this package and to run this command, so both must be what the
        // project actually packs.
        var project = File.ReadAllText(FindInRepository("src", "RepoHarness.Cli", "RepoHarness.Cli.csproj"));

        Assert.Contains($"<PackageId>{ToolPackage.Id}</PackageId>", project, StringComparison.Ordinal);
        Assert.Contains($"<ToolCommandName>{ToolPackage.Command}</ToolCommandName>", project, StringComparison.Ordinal);
        // Deliberately not compared with the assembly's own name. The command a host types and the
        // assembly this tool ships as were the same word by coincidence, and pinning that stopped
        // the command from being spelled the way a case-sensitive filesystem needs.
        Assert.Equal($"{ToolPackage.Id}.dll", Path.GetFileName(CliRunner.CliAssemblyPath));
    }

    private static async Task PrepareAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs =
            {
                ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" },

                // An operating system no declared host provides: this machine is the only host.
                ["elsewhere"] = new LegConfig
                {
                    Os = platform.PlatformKey == PlatformNames.Linux ? PlatformNames.MacOs : PlatformNames.Linux,
                    Processor = platform.Processor,
                    Config = "debug",
                },
            },
        });
    }

    private static string FindInRepository(params string[] parts)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(CliRunner.CliAssemblyPath)!); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail($"No {Path.Combine(parts)} was found above {CliRunner.CliAssemblyPath}.");
        return string.Empty;
    }
}
