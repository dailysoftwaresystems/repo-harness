using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RepoHarness.Core.Platform;

/// <summary>Owner-only access and executability on Windows.</summary>
/// <remarks>
/// A file does not become private by being created: it inherits whatever its directory
/// grants, and a repository can sit on any volume with any permissions. Protecting a
/// file therefore disables inheritance and replaces the whole access list with a single
/// entry for the current user.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsFilePermissions : IFilePermissions
{
    public void ProtectSecret(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Cannot protect a file that does not exist.", path);
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");

        var security = new FileSecurity();

        // Inherited entries are discarded rather than copied in: copying them would keep
        // exactly the access this method exists to remove.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));

        file.SetAccessControl(security);
    }

    public bool IsExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Windows has no execute bit. Whether a file runs is decided by its extension,
        // which the caller has already resolved through PATHEXT.
        return File.Exists(path);
    }
}
