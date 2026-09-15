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
    /// The configuration name for <see cref="Current"/> (<c>windows</c>, <c>linux</c>, <c>macos</c>):
    /// the key of a per-platform section, and the <c>os</c> a leg is compared with on this machine.
    /// </summary>
    string PlatformKey { get; }

    /// <summary>
    /// This machine's processor, in configuration's words (<c>x86_64</c>, <c>arm64</c>). The
    /// machine's own rather than this process's: repo-harness running as an x86_64 program under
    /// emulation on an arm64 machine is still on an arm64 machine.
    /// </summary>
    string Processor { get; }

    /// <summary>
    /// The current user's home directory, which a host path starting with <c>~</c> is relative to.
    /// </summary>
    string HomeDirectory { get; }

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
