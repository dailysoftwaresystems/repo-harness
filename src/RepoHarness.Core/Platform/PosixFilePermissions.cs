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

    /// <summary>Any access at all for the group or for others: what ssh calls an unprotected key.</summary>
    private const UnixFileMode GroupOrOtherAccess =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Write access for the group or for others: what lets another user rewrite a configuration file.</summary>
    private const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

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

    public bool IsPrivate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) && (File.GetUnixFileMode(path) & GroupOrOtherAccess) == 0;
    }

    public bool IsWritableByOthers(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) && (File.GetUnixFileMode(path) & GroupOrOtherWrite) != 0;
    }
}
