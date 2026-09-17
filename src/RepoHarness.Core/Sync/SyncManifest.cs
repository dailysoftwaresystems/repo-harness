using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Sync;

/// <summary>One file in a tree, identified by what it holds rather than by when it was written.</summary>
/// <param name="Path">The file's path relative to the tree root, with forward separators.</param>
/// <param name="Size">Its length in bytes.</param>
/// <param name="ContentHash">
/// Lowercase hex SHA-256 of its bytes. Content, never a timestamp: one host this tool serves has a
/// wall clock that steps forward by about 25 seconds every few seconds, and the steps reach file
/// modification times, so a file written after a marker can carry a stamp from before it. Equality
/// of content carries the same distortion on both sides and a clock cannot bend it.
/// </param>
public sealed record SyncEntry(string Path, long Size, string ContentHash);

/// <summary>The content of a tree, as the identity sync compares and confirms.</summary>
/// <param name="Root">The tree this manifest describes.</param>
/// <param name="Entries">Every transferable file, keyed by relative path.</param>
public sealed record SyncManifest(string Root, IReadOnlyDictionary<string, SyncEntry> Entries)
{
    /// <summary>
    /// Every link the walk refused to follow, by relative path. Not content, and never compared with
    /// anything: a link is neither transferred nor deleted. Recorded because a reader deciding
    /// whether to let this tool take a directory over cannot see them any other way — a link is in
    /// no entry, so a file written at its name replaces it and is reported as an ordinary write,
    /// and a directory behind one hides everything under it from the list entirely.
    /// </summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    /// <summary>An empty manifest, for a destination that does not exist yet.</summary>
    /// <param name="root">The tree the manifest would describe.</param>
    public static SyncManifest Empty(string root)
        => new(root, new Dictionary<string, SyncEntry>(StringComparer.Ordinal));

    /// <summary>The paths in this manifest, in a stable order.</summary>
    public IEnumerable<string> Paths => Entries.Keys.OrderBy(path => path, StringComparer.Ordinal);
}

/// <summary>Builds a manifest of a tree.</summary>
public interface IManifestBuilder
{
    /// <summary>Reads every transferable file under <paramref name="root"/> and hashes its content.</summary>
    /// <param name="root">The tree root.</param>
    /// <param name="isWithheld">
    /// Decides whether a relative path is withheld. Consulted on directories as well as files, so a
    /// withheld directory is never walked: walking a build tree to discard it costs the whole tree.
    /// </param>
    /// <param name="cancellationToken">Stops the walk.</param>
    Task<SyncManifest> BuildAsync(
        string root,
        Func<string, bool> isWithheld,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IManifestBuilder"/>
public sealed class ManifestBuilder(IFileSystem fileSystem, IHostPlatform platform) : IManifestBuilder
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <summary>
    /// Turns an absolute path into the form a manifest keys by: relative to the root, with forward
    /// separators.
    /// </summary>
    /// <param name="root">The tree root.</param>
    /// <param name="path">A path inside it.</param>
    /// <remarks>
    /// Forward separators on every platform, because the two sides of a sync frequently do not agree
    /// about separators: a Windows machine syncing to a WSL distribution would otherwise find no
    /// path in common and rewrite the whole tree on every run.
    /// </remarks>
    public static string Relative(string root, string path)
        => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <inheritdoc/>
    public async Task<SyncManifest> BuildAsync(
        string root,
        Func<string, bool> isWithheld,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isWithheld);

        var entries = new Dictionary<string, SyncEntry>(StringComparer.Ordinal);

        if (!_fileSystem.DirectoryExists(root))
        {
            return SyncManifest.Empty(root);
        }

        var links = new List<string>();

        await WalkAsync(root, root, isWithheld, entries, links, cancellationToken).ConfigureAwait(false);

        links.Sort(StringComparer.Ordinal);

        return new SyncManifest(root, entries) { Links = links };
    }

    private async Task WalkAsync(
        string root,
        string directory,
        Func<string, bool> isWithheld,
        Dictionary<string, SyncEntry> entries,
        List<string> links,
        CancellationToken cancellationToken)
    {
        foreach (var file in _fileSystem.EnumerateFiles(directory, recursive: false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Relative(root, file);

            if (isWithheld(relative))
            {
                continue;
            }

            // A link is not followed, for a file as much as for a directory. Opening one reads
            // whatever it points at, so a link committed to the repository and aimed outside it
            // would have its target's bytes hashed here and written to another machine under the
            // link's own name — the repository's content deciding what leaves this machine.
            if (IsLink(file))
            {
                links.Add(relative);
                continue;
            }

            entries[relative] = await DescribeAsync(relative, file, cancellationToken).ConfigureAwait(false);
        }

        foreach (var child in _fileSystem.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Relative(root, child);

            if (isWithheld(relative))
            {
                continue;
            }

            // A directory link inside a tree makes the walk unbounded, and one that leaves the tree
            // would put files outside it into the manifest.
            if (IsLink(child))
            {
                links.Add(relative);
                continue;
            }

            await WalkAsync(root, child, isWithheld, entries, links, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SyncEntry> DescribeAsync(string relative, string file, CancellationToken cancellationToken)
    {
        var content = await FileContentHash.OfAsync(_fileSystem, file, cancellationToken).ConfigureAwait(false);

        return new SyncEntry(relative, content.Length, content.Content);
    }

    /// <summary>
    /// Whether <paramref name="path"/> reaches somewhere other than itself, so that reading or
    /// walking it would act on something the tree does not contain.
    /// </summary>
    /// <remarks>
    /// Asked of a file as well as a directory. Every link along the path is followed and the answer
    /// compared with the path as spelled, so a link whose target happens to sit inside the tree is
    /// still not transferred: what would arrive on the other machine is a copy of the target under
    /// the link's name, which is not what the source holds.
    /// </remarks>
    private bool IsLink(string path) => LinkPaths.IsLink(_fileSystem, path, _platform.PathComparison);
}
