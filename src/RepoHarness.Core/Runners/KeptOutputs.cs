using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Runners;

/// <summary>The files a run's steps kept, as a tree names them.</summary>
public static class KeptOutputs
{
    /// <summary>
    /// Each file below <paramref name="directory"/>, relative to <paramref name="treeRoot"/> with forward slashes,
    /// in order: the exact path <c>sync --pull</c> takes. None where the directory is not there.
    /// </summary>
    /// <param name="fileSystem">Lists the files.</param>
    /// <param name="treeRoot">The tree the paths are relative to.</param>
    /// <param name="directory">A directory of kept outputs inside that tree.</param>
    public static IReadOnlyList<string> Under(IFileSystem fileSystem, string treeRoot, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return fileSystem.DirectoryExists(directory)
            ? [.. fileSystem
                .EnumerateFiles(directory, recursive: true)
                .Select(file => PathPatterns.Normalize(Path.GetRelativePath(treeRoot, file)))
                .Order(StringComparer.Ordinal)]
            : [];
    }
}
