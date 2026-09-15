using System.Runtime.InteropServices;

namespace RepoHarness.Core.Platform;

/// <inheritdoc cref="IHostPlatform"/>
public sealed class HostPlatform : IHostPlatform
{
    /// <summary>Windows refuses most paths beyond this length.</summary>
    public const int WindowsMaxPath = 260;

    public HostPlatform()
    {
        Current = ResolveCurrent();
    }

    public PlatformId Current { get; }

    public string PlatformKey => Current switch
    {
        PlatformId.Windows => PlatformNames.Windows,
        PlatformId.Linux => PlatformNames.Linux,
        PlatformId.MacOs => PlatformNames.MacOs,
        _ => throw new InvalidOperationException($"Unmapped platform '{Current}'."),
    };

    public string Processor { get; } = PlatformNames.ForArchitecture(RuntimeInformation.OSArchitecture);

    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public StringComparison PathComparison => Current == PlatformId.Windows
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public int? MaxPathLength => Current == PlatformId.Windows ? WindowsMaxPath : null;

    public string ExecutableName(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (Current != PlatformId.Windows)
        {
            return command;
        }

        return Path.HasExtension(command) ? command : command + ".exe";
    }

    private static PlatformId ResolveCurrent()
    {
        if (OperatingSystem.IsWindows())
        {
            return PlatformId.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return PlatformId.MacOs;
        }

        if (OperatingSystem.IsLinux())
        {
            return PlatformId.Linux;
        }

        throw new PlatformNotSupportedException(
            "DssHarness supports Windows, Linux and macOS only.");
    }
}
