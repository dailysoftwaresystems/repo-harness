using System.Runtime.InteropServices;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// A leg names what it needs and a host is measured into the same words. A report that uses
/// another vendor's word for the same processor must still compare equal, or a host that can
/// run a leg is reported as one that cannot.
/// </summary>
public sealed class PlatformNamesTests
{
    [Theory]
    [InlineData("x86_64", "x86_64")]
    [InlineData("amd64", "x86_64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("arm64", "arm64")]
    [InlineData("i686", "x86")]
    [InlineData("armv7l", "arm")]
    [InlineData("riscv64\n", "riscv64")]
    public void ForMachine_TranslatesWhatUnameReports(string machine, string expected)
    {
        Assert.Equal(expected, PlatformNames.ForMachine(machine));
    }

    [Fact]
    public void ForMachine_ReturnsNull_ForAProcessorConfigurationCannotName()
    {
        Assert.Null(PlatformNames.ForMachine("pdp11"));
    }

    [Theory]
    [InlineData("Linux", "linux")]
    [InlineData("Darwin", "macos")]
    public void ForKernel_TranslatesWhatUnameReports(string kernel, string expected)
    {
        Assert.Equal(expected, PlatformNames.ForKernel(kernel));
    }

    [Fact]
    public void ForKernel_ReturnsNull_ForAnOperatingSystemConfigurationCannotName()
    {
        Assert.Null(PlatformNames.ForKernel("FreeBSD"));
    }

    [Theory]
    [InlineData(Architecture.X64, "x86_64")]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.X86, "x86")]
    [InlineData(Architecture.Arm, "arm")]
    public void ForArchitecture_TranslatesWhatDotnetReports(Architecture architecture, string expected)
    {
        Assert.Equal(expected, PlatformNames.ForArchitecture(architecture));
    }

    [Fact]
    public void EveryNameATranslationProduces_IsOneConfigurationAccepts()
    {
        // A measurement the validator would refuse as a leg's value could never match any leg.
        var produced = Enum.GetValues<Architecture>()
            .Where(architecture => architecture != Architecture.Wasm)
            .Select(PlatformNames.ForArchitecture);

        Assert.All(produced, name => Assert.Contains(name, PlatformNames.Processors));
    }

    [Fact]
    public void HostPlatform_ReportsTheMachinesOwnProcessor()
    {
        // Expected from RuntimeInformation directly: asking the code under test what to expect
        // could never fail.
        var expected = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };

        Assert.Equal(expected, new HostPlatform().Processor);
    }
}
