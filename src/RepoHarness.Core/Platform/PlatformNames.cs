using System.Runtime.InteropServices;

namespace RepoHarness.Core.Platform;

/// <summary>
/// The words configuration uses for operating systems and processors, and how what a machine
/// reports becomes those words. A leg names what it needs in them and a host is measured into
/// them, so the two compare without either side guessing.
/// </summary>
public static class PlatformNames
{
    /// <summary>Windows.</summary>
    public const string Windows = "windows";

    /// <summary>Linux, including a WSL distribution.</summary>
    public const string Linux = "linux";

    /// <summary>macOS.</summary>
    public const string MacOs = "macos";

    /// <summary>64-bit x86, whatever a vendor calls it: amd64, x64, x86_64.</summary>
    public const string X64 = "x86_64";

    /// <summary>64-bit Arm, whatever a vendor calls it: aarch64, arm64.</summary>
    public const string Arm64 = "arm64";

    /// <summary>Every operating system a host can be measured as.</summary>
    public static IReadOnlyList<string> OperatingSystems { get; } = [Windows, Linux, MacOs];

    /// <summary>Every processor a host can be measured as.</summary>
    public static IReadOnlyList<string> Processors { get; } =
        [X64, Arm64, "x86", "arm", "riscv64", "loongarch64", "ppc64le", "s390x"];

    /// <summary>The name of a processor as .NET reports it.</summary>
    public static string ForArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => X64,
        Architecture.Arm64 => Arm64,
        Architecture.X86 => "x86",
        Architecture.Arm or Architecture.Armv6 => "arm",
        Architecture.RiscV64 => "riscv64",
        Architecture.LoongArch64 => "loongarch64",
        Architecture.Ppc64le => "ppc64le",
        Architecture.S390x => "s390x",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The name of a processor as <c>uname -m</c> reports it, or <see langword="null"/> when it is
    /// none of <see cref="Processors"/>.
    /// </summary>
    public static string? ForMachine(string machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        return machine.Trim() switch
        {
            "x86_64" or "amd64" => X64,
            "aarch64" or "arm64" => Arm64,
            "i386" or "i486" or "i586" or "i686" => "x86",
            "armv6l" or "armv7l" or "armv8l" => "arm",
            "riscv64" => "riscv64",
            "loongarch64" => "loongarch64",
            "ppc64le" => "ppc64le",
            "s390x" => "s390x",
            _ => null,
        };
    }

    /// <summary>
    /// The name of an operating system as <c>uname -s</c> reports it, or <see langword="null"/> when
    /// it is none of <see cref="OperatingSystems"/>.
    /// </summary>
    public static string? ForKernel(string kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        return kernel.Trim() switch
        {
            "Linux" => Linux,
            "Darwin" => MacOs,
            _ => null,
        };
    }
}
