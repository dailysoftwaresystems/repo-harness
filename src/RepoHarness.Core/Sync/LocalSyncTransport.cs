using RepoHarness.Core.Platform;
using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>
/// A sync into a directory on the machine running the harness. Also what a host runs on its own
/// side, so one implementation serves every transport and none of them can drift.
/// </summary>
public sealed class LocalSyncTransport(
    IFileSystem fileSystem,
    IManifestBuilder manifestBuilder,
    IGitClient gitClient,
    IHostPlatform platform) : ISyncTransport
{
    /// <summary>
    /// The file recording that the harness made this copy, inside the copy's own harness directory,
    /// which is withheld from transfer and so can never be overwritten by the source.
    /// </summary>
    public const string MarkerFileName = "synced-copy.json";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IManifestBuilder _manifestBuilder = manifestBuilder;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IHostPlatform _platform = platform;

    /// <inheritdoc/>
    public HostId Host { get; } = HostId.Local;

    /// <inheritdoc/>
    public Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default)
        => Task.FromResult(_fileSystem.DirectoryExists(Home(root)));

    /// <inheritdoc/>
    public Task CreateRootAsync(string root, CancellationToken cancellationToken = default)
    {
        // A file where the directory should be is named rather than worked around. Creating the
        // copy somewhere else would leave a leg reporting on a tree nobody can find.
        if (_fileSystem.FileExists(Home(root)))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{root}' exists and is a file, so the repository copy cannot be created there.");
        }

        try
        {
            _fileSystem.CreateDirectory(Home(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{root}' could not be created: {ex.Message}");
        }

        _fileSystem.WriteAllTextAtomic(
            MarkerPath(root),
            JsonSerializer.Serialize(
                new SyncedCopyMarker(DateTimeOffset.UtcNow.ToString("O"), Environment.MachineName),
                MarkerOptions));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> IsHarnessCopyAsync(string root, CancellationToken cancellationToken = default)
        => Task.FromResult(_fileSystem.FileExists(MarkerPath(root)));

    /// <inheritdoc/>
    public async Task InitialiseRepositoryAsync(string root, CancellationToken cancellationToken = default)
    {
        if (await _gitClient.IsRepositoryAsync(Home(root), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var result = await _gitClient
            .RunAsync(Home(root), ["init", "--quiet", "."], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{root}' could not be made a git repository, which the harness there needs to find "
                + $"anything: {result.FailureMessage}");
        }
    }

    /// <inheritdoc/>
    public Task<SyncManifest> ReadManifestAsync(
        string root,
        IReadOnlyList<string> withheld,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(withheld);

        var policy = new PathSet(withheld);

        return _manifestBuilder.BuildAsync(Home(root), policy.Contains, cancellationToken);
    }

    /// <inheritdoc/>
    public Task WriteFileAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken = default)
        => _fileSystem.WriteAllBytesAtomicAsync(Resolve(root, relativePath), contents, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        _fileSystem.DeleteFile(Resolve(root, relativePath));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<byte[]> ReadFileAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = _fileSystem.OpenRead(Resolve(root, relativePath));
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>
    /// Joins a relative path to the copy's root and refuses anything that would land outside it.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="relativePath">The path to resolve, relative to the root.</param>
    /// <exception cref="HarnessException">The path leaves the tree.</exception>
    /// <remarks>
    /// Deletion is confined to the transferred tree, and this is where that is enforced. A path
    /// holding <c>..</c> is what turns a deletion inside a repository copy into a deletion of
    /// whatever sits beside it, and the source of a path is a manifest the far side produced.
    /// </remarks>
    public string Resolve(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{relativePath}' is an absolute path, and a sync acts only inside the tree it was given.");
        }

        var expanded = Home(root);
        var full = Path.GetFullPath(Path.Combine(expanded, relativePath));

        // This machine's own comparison. Testing both would be no test at all: a path inside under
        // Ordinal is inside under OrdinalIgnoreCase too, so the pair reduces to the looser of them,
        // and on Linux a path differing only in case would escape the tree.
        if (!PathContainment.IsStrictlyInside(expanded, full, _platform.PathComparison))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{relativePath}' resolves outside '{root}', so a sync will not touch it.");
        }

        return full;
    }

    /// <summary>
    /// A declared path with a leading <c>~</c> expanded to this machine's home directory.
    /// </summary>
    /// <param name="root">The path as the configuration declares it.</param>
    /// <remarks>
    /// <c>hosts.*.repositoryPath</c> is documented with <c>~/</c> and the README's own example uses
    /// it. Passed through unexpanded it becomes a directory literally named <c>~</c> beside wherever
    /// the tool happened to run, while the host agent — which does expand it — looks in the home
    /// directory: the sync and the run then disagree about where the tree is.
    /// </remarks>
    internal static string Home(string root)
    {
        if (root != "~" && !root.StartsWith("~/", StringComparison.Ordinal) && !root.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return root;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return home.Length == 0 ? root : Path.Combine(home, root.Length <= 2 ? string.Empty : root[2..]);
    }

    private static string MarkerPath(string root)
        => Path.Combine(Home(root), HarnessLayout.DirectoryName, MarkerFileName);

    private static readonly JsonSerializerOptions MarkerOptions = new() { WriteIndented = true };

    /// <summary>What the marker file records, so a reader can tell where a copy came from.</summary>
    /// <param name="CreatedUtc">When the harness created this copy.</param>
    /// <param name="CreatedBy">The machine that created it.</param>
    private sealed record SyncedCopyMarker(string CreatedUtc, string CreatedBy);

    /// <summary>A withheld-path test built once, rather than re-parsed for every file in a tree.</summary>
    private sealed class PathSet(IReadOnlyList<string> paths)
    {
        private readonly string[] _paths = [.. paths
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .Where(path => path.Length > 0)];

        public bool Contains(string relativePath)
        {
            var candidate = relativePath.Replace('\\', '/').Trim('/');

            return _paths.Any(path =>
                string.Equals(candidate, path, StringComparison.Ordinal)
                || candidate.StartsWith(path + "/", StringComparison.Ordinal));
        }
    }
}
