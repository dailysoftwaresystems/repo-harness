using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// An emulator counts only once its witness proves it runs programs for its processor. The launcher
/// here is this test assembly started as a child, standing in for qemu: it prints its argument, so
/// each test decides exactly what the witness prints.
/// </summary>
public sealed class EmulatorProbeTests
{
    [Fact]
    public async Task CheckAsync_Passes_WhenTheWitnessPrintsWhatOnlyTheEmulatedProcessorWould()
    {
        var check = await Probe().CheckAsync(Emulator(prints: "aarch64", pattern: @"^\[aarch64\]$"), TestContext.Current.CancellationToken);

        Assert.True(check.Available, check.Reason);
        Assert.Equal("[aarch64]", check.Witnessed);
    }

    [Fact]
    public async Task CheckAsync_Refuses_AWitnessWhoseOutputDoesNotMatch()
    {
        // A launcher that quietly ran the program natively would print the host's own processor.
        var check = await Probe().CheckAsync(Emulator(prints: "x86_64", pattern: @"^\[aarch64\]$"), TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Contains("does not match", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_Refuses_AnEmulatorForAnotherKindOfHost_WithoutRunningAnything()
    {
        var runner = Substitute.For<IProcessRunner>();
        var probe = new EmulatorProbe(Platform("macos", "arm64"), runner, FileSystem());

        var check = await probe.CheckAsync(Emulator(prints: "aarch64", pattern: "x"), TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Contains("it runs on linux x86_64 hosts, and this one is macos arm64", check.Reason, StringComparison.Ordinal);
        _ = runner.DidNotReceiveWithAnyArgs().RunAsync(null!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("definitely-not-a-real-tool-xyzzy")]
    [InlineData("/definitely/not/a/sysroot")]
    public async Task CheckAsync_NamesARequirementThatIsMissing(string requirement)
    {
        var check = await Probe().CheckAsync(
            Emulator(prints: "aarch64", pattern: "x", requires: [requirement]),
            TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Equal($"{requirement} is missing", check.Reason);
    }

    [Fact]
    public async Task CheckAsync_ReportsAWitnessThatFails()
    {
        var emulator = new EmulatorConfig
        {
            HostOs = "linux",
            HostProcessor = "x86_64",
            Processor = "arm64",
            Launcher = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath],
            Env = { [TestHost.ChildModeVariable] = "exit" },
            Witness = new EmulatorWitness { Command = ["7"], Pattern = "x" },
        };

        var check = await Probe().CheckAsync(emulator, TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Contains("exited 7", check.Reason, StringComparison.Ordinal);
    }

    private static EmulatorConfig Emulator(string prints, string pattern, List<string>? requires = null) => new()
    {
        HostOs = "linux",
        HostProcessor = "x86_64",
        Processor = "arm64",
        Launcher = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath],
        Env = { [TestHost.ChildModeVariable] = "echo-args" },
        Requires = requires ?? [],
        Witness = new EmulatorWitness { Command = [prints], Pattern = pattern },
    };

    private static EmulatorProbe Probe()
        => new(Platform("linux", "x86_64"), new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create()), FileSystem());

    private static IHostPlatform Platform(string os, string processor)
    {
        var platform = Substitute.For<IHostPlatform>();
        platform.PlatformKey.Returns(os);
        platform.Processor.Returns(processor);
        return platform;
    }

    private static PhysicalFileSystem FileSystem() => new(FilePermissionsFactory.Create());
}
