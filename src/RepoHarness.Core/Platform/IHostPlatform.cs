namespace RepoHarness.Core.Platform;

/// <summary>
/// The primary place where the current operating system is observed. Only
/// <see cref="FilePermissionsFactory"/> also calls <c>OperatingSystem.IsWindows()</c>,
/// and only so the platform-compatibility analyser can prove a POSIX-only call site
/// unreachable on Windows. No other type may: platform decisions are centralised here
/// so command and domain logic stay platform agnostic.
/// </summary>
public interface IHostPlatform
{
    /// <summary>The operating system family this process is running on.</summary>
    PlatformId Current { get; }

    /// <summary>
    /// The configuration key for <see cref="Current"/> (<c>windows</c>, <c>linux</c>, <c>macos</c>).
    /// Used to select per-platform sections in config.json.
    /// </summary>
    string PlatformKey { get; }

    /// <summary>Network name of this machine, used to evaluate SSH <c>availableOn</c> rules.</summary>
    string HostName { get; }

    /// <summary>How file system paths compare on this platform (case insensitive on Windows).</summary>
    StringComparison PathComparison { get; }

    /// <summary>
    /// Maximum usable path length, or <see langword="null"/> where the platform imposes no
    /// practical limit. Windows returns 260: exceeding it surfaces as compile errors in
    /// files a build never touched, so callers budget against it before creating trees.
    /// </summary>
    int? MaxPathLength { get; }

    /// <summary>Appends the platform's executable suffix when one is required.</summary>
    string ExecutableName(string command);
}
