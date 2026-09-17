namespace RepoHarness.Core.FileSystem;

/// <summary>
/// Whether a path reaches somewhere other than itself.
/// </summary>
/// <remarks>
/// One test, because every walk of a tree needs it and needs the same answer. A directory link
/// inside a tree makes a walk unbounded, and one that leaves the tree puts files outside it into
/// whatever the walk was collecting; a walk that asks the question differently from its neighbour is
/// a walk that is unbounded on a tree its neighbour handles.
/// </remarks>
public static class LinkPaths
{
    /// <summary>
    /// Whether <paramref name="path"/> reaches somewhere other than itself, so that reading or
    /// walking it would act on something the tree does not contain.
    /// </summary>
    /// <param name="fileSystem">Resolves the path.</param>
    /// <param name="path">The path to test.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    public static bool IsLink(IFileSystem fileSystem, string path, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var resolved = fileSystem.ResolveLinks(path);

        return !string.Equals(
            Path.TrimEndingDirectorySeparator(resolved),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            comparison);
    }
}
