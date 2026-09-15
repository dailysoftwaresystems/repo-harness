using System.Runtime.Versioning;

namespace RepoHarness.Core.Platform;

/// <summary>
/// Owner-only access and execute bits on Linux and macOS, which share the Unix mode model.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class PosixFilePermissions : IFilePermissions
{
    private const UnixFileMode AnyExecute =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    public void ProtectSecret(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public bool IsExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Without this, a non-executable file that happens to be called "git" earlier on
        // PATH would be reported as git being installed, and every later invocation would
        // fail with a permission error that points nowhere near the cause.
        return File.Exists(path) && (File.GetUnixFileMode(path) & AnyExecute) != 0;
    }
}
