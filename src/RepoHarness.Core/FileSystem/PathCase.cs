namespace RepoHarness.Core.FileSystem;

/// <summary>How a file system compares the names of what it holds, as it answers for itself.</summary>
public static class PathCase
{
    /// <summary>
    /// How paths compare where <paramref name="directory"/> is: ignoring case where the file system
    /// finds that directory by its name in another case - Windows', and macOS's by default - and
    /// exactly where it does not.
    /// </summary>
    /// <remarks>
    /// Asked of the file system rather than decided by the operating system: macOS's default volume
    /// finds a file by its name in any case where one of its case-sensitive volumes does not, and a
    /// directory on Windows can be made case-sensitive. A path with no letter whose case can change
    /// compares exactly.
    /// </remarks>
    /// <param name="fileSystem">The file system to ask.</param>
    /// <param name="directory">A directory that exists, whose file system is asked.</param>
    public static StringComparer In(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(directory);

        var otherCase = directory.ToUpperInvariant();

        if (otherCase == directory)
        {
            otherCase = directory.ToLowerInvariant();
        }

        return otherCase != directory && fileSystem.DirectoryExists(otherCase)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }
}
