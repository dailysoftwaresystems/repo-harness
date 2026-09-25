using Microsoft.Win32;

namespace RepoHarness.Core.Hosts;

/// <summary>Where WSL keeps a distribution's disk on this machine.</summary>
public interface IWslDiskImages
{
    /// <summary>
    /// The directory on this machine holding <paramref name="distribution"/>'s disk, or <see langword="null"/>
    /// where WSL names none: this machine is not Windows, or no distribution of that name is registered.
    /// </summary>
    /// <param name="distribution">The distribution, by name, in whatever case.</param>
    string? DirectoryOf(string distribution);
}

/// <summary>Reads where WSL keeps each distribution's disk from what WSL registers for this user.</summary>
/// <remarks>
/// A WSL 2 distribution's filesystem is a virtual disk file that grows on the Windows drive holding it, and
/// what the distribution measures is the virtual disk's own room - a terabyte, by default - whatever that drive
/// has left. WSL registers each distribution under the user's <c>Lxss</c> key, its name as
/// <c>DistributionName</c> and the directory holding its disk as <c>BasePath</c>.
/// </remarks>
public sealed class WslDiskImages : IWslDiskImages
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public string? DirectoryOf(string distribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distribution);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey);

        foreach (var id in lxss?.GetSubKeyNames() ?? [])
        {
            using var entry = lxss!.OpenSubKey(id);

            if (string.Equals(entry?.GetValue("DistributionName") as string, distribution, StringComparison.OrdinalIgnoreCase)
                && entry?.GetValue("BasePath") is string { Length: > 0 } basePath)
            {
                return Plain(basePath);
            }
        }

        return null;
    }

    /// <summary><paramref name="basePath"/> without the <c>\\?\</c> WSL writes before some of them, which no drive lookup takes.</summary>
    /// <param name="basePath">A <c>BasePath</c> as WSL registered it.</param>
    public static string Plain(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);

        return basePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? basePath[4..] : basePath;
    }
}
