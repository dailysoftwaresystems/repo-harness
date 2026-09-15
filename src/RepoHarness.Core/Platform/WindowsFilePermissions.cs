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
    /// <summary>The right that lets an identity see a file's contents.</summary>
    private const FileSystemRights ReadRights = FileSystemRights.ReadData;

    /// <summary>The rights that let an identity change a file's contents.</summary>
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData;

    public void ProtectSecret(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Cannot protect a file that does not exist.", path);
        }

        var security = new FileSecurity();

        // Inherited entries are discarded rather than copied in: copying them would keep
        // exactly the access this method exists to remove.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));

        file.SetAccessControl(security);
    }

    public bool IsExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Windows has no execute bit. Whether a file runs is decided by its extension,
        // which the caller has already chosen.
        return File.Exists(path);
    }

    public bool IsPrivate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) && !GrantsOthers(path, ReadRights);
    }

    public bool IsWritableByOthers(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) && GrantsOthers(path, WriteRights);
    }

    /// <summary>
    /// Whether any access rule, explicit or inherited, allows <paramref name="rights"/> to someone
    /// other than the current user, SYSTEM or the Administrators group: the identities OpenSSH for
    /// Windows itself accepts on a private key.
    /// </summary>
    private static bool GrantsOthers(string path, FileSystemRights rights)
    {
        var accepted = new HashSet<SecurityIdentifier>
        {
            CurrentUser(),
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };

        var rules = new FileInfo(path)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & rights) != 0
                && rule.IdentityReference is SecurityIdentifier grantee
                && !accepted.Contains(grantee))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The current user's security identifier, which outlives the identity it was read from.</summary>
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();

        return identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");
    }
}
