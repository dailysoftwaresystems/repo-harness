using RepoHarness.Core.Platform;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default)
    {
        // None is not a mark a copy can carry: it says there is no marker at all, and encoding it
        // would write one claiming the copy was both taken over and finished — the most permissive
        // thing this file can say, and the one nobody asked for. Checked before anything is created,
        // so a caller that gets this wrong leaves nothing behind.
        if (mark is not (CopyMark.Complete or CopyMark.AdoptionStopped))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mark),
                mark,
                "A copy is created as a complete one or as a takeover that has begun, never as unmarked.");
        }

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
                new SyncedCopyMarker(
                    DateTimeOffset.UtcNow.ToString("O"),
                    Environment.MachineName,
                    Adopted: mark == CopyMark.AdoptionStopped || Adopted(root),
                    Completed: mark != CopyMark.AdoptionStopped),
                MarkerOptions));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<SyncInspectAnswer> InspectAsync(string root, CancellationToken cancellationToken = default)
        => new(
            await RootExistsAsync(root, cancellationToken).ConfigureAwait(false),
            await ReadMarkAsync(root, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc/>
    public Task<CopyMark> ReadMarkAsync(string root, CancellationToken cancellationToken = default)
        => Task.FromResult(Marker(root) switch
        {
            // An unfinished takeover is told apart from a finished copy, because only one of them
            // still needs somebody to say go ahead.
            null => CopyMark.None,
            { Adopted: true, Completed: false } => CopyMark.AdoptionStopped,
            _ => CopyMark.Complete,
        });

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
    public async Task WriteFileAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _fileSystem
                .WriteAllBytesAtomicAsync(Resolve(root, relativePath), contents, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Named here rather than in the loop that drives the sync, because for every host but
            // this one the write happens on the far side and only the far side's message comes
            // back. Unnamed, the reader gets an exception from somewhere inside a write with
            // neither the path nor what was being written to it.
            //
            // The likeliest cause is offered as a possibility, not asserted: the two halves of it
            // arrive as different exceptions — a file where this tree has a directory fails the
            // directory's creation, a directory where it has a file fails the replace — and a full
            // disk, a read-only mount and a file another process holds open all arrive as one of
            // the same two types. Asserting would send the reader looking for a collision that is
            // not there while the disk stays full.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' could not be written to '{root}': {ex.Message} If something along "
                + "that path is there as the other kind of thing — a file where this tree has a "
                + "directory, or a directory where it has a file — remove it there and sync again.",
                ex);
        }
    }

    /// <inheritdoc/>
    public Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        _fileSystem.DeleteFile(Resolve(root, relativePath));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EmptiedDirectory>> RemoveEmptyDirectoriesAsync(
        string root,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var expanded = Home(root);
        var answered = new List<EmptiedDirectory>();

        // Deepest first, so a directory that empties only because its child went is considered
        // after that child has gone rather than before.
        foreach (var directory in directories
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(path => path.Count(character => character is '/' or '\\'))
            .ThenBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Resolved the same way every other path a sync acts on is, so one arriving with '..'
            // cannot reach a directory beside the copy.
            var full = Resolve(root, directory);
            var relative = directory;

            // Only the directory the plan emptied is answered for. The walk continues upward to take
            // parents the removal has just emptied in turn, but a parent that still holds something
            // is the ordinary case — it is where the tree keeps its other files — and reporting it
            // as a copy that diverges would put a false warning on almost every sync that deletes a
            // nested directory.
            var emptiedHere = true;

            while (PathContainment.IsStrictlyInside(expanded, full, _platform.PathComparison))
            {
                // Files and subdirectories both, and links among them: EnumerateFiles lists a link
                // to a file and EnumerateDirectories lists a link to a directory, so a directory
                // holding nothing but a link is correctly not empty. That is the case the manifest
                // cannot see, and the one where being wrong deletes what no plan can speak for.
                if (!_fileSystem.DirectoryExists(full))
                {
                    break;
                }

                var held = _fileSystem
                    .EnumerateFiles(full, recursive: false)
                    .Concat(_fileSystem.EnumerateDirectories(full))
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Order(StringComparer.Ordinal)
                    .Take(HeldNamesReported)
                    .ToList();

                if (held.Count > 0)
                {
                    if (emptiedHere)
                    {
                        // Answered for rather than passed over. This is the shape a consumer measured:
                        // a directory whose every managed file the plan deleted, kept alive by bytecode
                        // that sync.neverTransfer protects, so the checkout diverges from this tree and
                        // the next structural check on that host fails with nothing naming the cause.
                        answered.Add(EmptiedDirectory.Kept(relative.Replace('\\', '/'), held!));
                    }

                    break;
                }

                _fileSystem.DeleteDirectory(full);
                answered.Add(EmptiedDirectory.Gone(relative.Replace('\\', '/')));

                if (Path.GetDirectoryName(full) is not { Length: > 0 } parent)
                {
                    break;
                }

                full = parent;
                relative = ManifestBuilder.Relative(expanded, parent);
                emptiedHere = false;
            }
        }

        return Task.FromResult<IReadOnlyList<EmptiedDirectory>>(answered);
    }

    /// <summary>
    /// How many of a surviving directory's names are reported. Enough to recognise what kept it
    /// without printing somebody's whole cache.
    /// </summary>
    private const int HeldNamesReported = 5;

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

        // Compared as spelled, deliberately. Following links here looked like a stronger guard and
        // was a worse one: a build directory pointed at another volume is ordinary and withheld by
        // default for exactly that reason, and resolving it refused '--pull build/...' — a path the
        // reader named, inside the tree they named — with a message about leaving the tree. A link
        // the copy holds is disclosed where the reader can act on it, in the list an adoption prints,
        // rather than guarded here where it cannot be told from a path somebody meant.
        //
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
        if (root != "~" && !PlatformPaths.IsHomeRelative(root))
        {
            return root;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return home.Length == 0 ? root : Path.Combine(home, root.Length <= 2 ? string.Empty : root[2..]);
    }

    private static string MarkerPath(string root)
        => Path.Combine(Home(root), HarnessLayout.DirectoryName, MarkerFileName);

    private static readonly JsonSerializerOptions MarkerOptions = new()
    {
        WriteIndented = true,

        // A shape this build does not recognise is a hard failure rather than silent data loss, as
        // it is wherever this tool reads JSON that decides something. This file decides whose
        // directory a sync is about to delete into, so it is the last one that should guess.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>What the marker file records, so a reader can tell where a copy came from.</summary>
    /// <param name="CreatedUtc">When the harness created this copy.</param>
    /// <param name="CreatedBy">The machine that created it.</param>
    /// <param name="Adopted">
    /// Whether it took over a directory that was already there, deleting what the source did not
    /// have, rather than creating an empty one. Afterwards the two are indistinguishable, and only
    /// one of them destroyed something.
    /// </param>
    /// <param name="Completed">
    /// Whether the run that made it got to the end. A takeover that stopped part way is marked but
    /// not complete, which is neither the checkout somebody had nor a copy of the source.
    /// </param>
    /// <remarks>
    /// Both strings are nullable because deserialising decides that, not this declaration: a file
    /// holding <c>{}</c> parses into a marker with neither, and that is one of the shapes that has
    /// to be told from a marker this tool wrote. Every marker it has ever written carries both.
    /// </remarks>
    private sealed record SyncedCopyMarker(string? CreatedUtc, string? CreatedBy, bool Adopted, bool Completed);

    /// <summary>
    /// What the marker at <paramref name="root"/> says, or null where there is none.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <exception cref="HarnessException">A marker is there and cannot be read.</exception>
    /// <remarks>
    /// Every unreadable shape refuses, and that direction is the whole point.
    /// <see cref="CopyMark.Complete"/> is the most permissive answer this can produce — it is what
    /// lets an ordinary <c>build</c> or <c>test</c>, which never carries an adopt list, delete a
    /// directory without asking anybody — so a damaged marker must never reach it. Reading one as
    /// complete because this build wrote it gets the inference backwards: a marker that cannot be
    /// parsed says nothing about who wrote it, and quite a lot about the record of whose directory
    /// this is having been lost. The reader loses nothing by being asked, and the one flag that
    /// answers is already in the message.
    /// <para>
    /// A marker from before takeovers existed holds neither <c>Adopted</c> nor <c>Completed</c>,
    /// which is a copy this tool made and finished. So a missing member stays legal while an
    /// unknown one does not, and the two are told apart by <c>CreatedUtc</c>, which every marker
    /// this tool has ever written carries.
    /// </para>
    /// </remarks>
    private SyncedCopyMarker? Marker(string root)
    {
        var path = MarkerPath(root);

        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        SyncedCopyMarker? marker;

        try
        {
            marker = JsonSerializer.Deserialize<SyncedCopyMarker>(_fileSystem.ReadAllText(path), MarkerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Damaged(path, root, ex.Message, ex);
        }

        return marker is null or { CreatedUtc: null } or { CreatedBy: null }
            ? throw Damaged(path, root, "it does not hold what this tool writes there.", inner: null)
            : marker;
    }

    private static HarnessException Damaged(string path, string root, string why, Exception? inner)
        => new(
            HarnessExit.Refused,
            $"'{path}' records how '{root}' came to be, and this build cannot read it: {why} Until it "
            + "can be read there is no telling a copy this tool made from a checkout it would be "
            + "deleting into, so nothing was changed. Delete that file to have the directory treated "
            + "as one this tool did not make, which '--adopt' can then take over after listing what "
            + "it would cost.",
            inner);

    /// <summary>
    /// Whether the marker already there says this copy was taken over. Read when one is being
    /// written to say a takeover finished, so finishing does not erase how the copy came to be.
    /// </summary>
    /// <remarks>
    /// Refuses rather than answering no when the marker cannot be read. By the time this runs the
    /// takeover has already succeeded, so failing costs an exit code; answering no would write a
    /// marker indistinguishable from one for a copy made in an empty directory, and this file is
    /// the only thing left that can say somebody's files were deleted to make it.
    /// </remarks>
    private bool Adopted(string root) => Marker(root) is { Adopted: true };

    /// <summary>A withheld-path test over a list put into its comparison form once.</summary>
    /// <remarks>
    /// Through the one matcher the plan is built with, not a second copy of the rule. The walk on a
    /// host and the plan on the asking machine have to agree about what is covered, and two
    /// implementations are how a file ends up protected by one and deleted by the other.
    /// <para>
    /// The list is normalised here rather than on every comparison. A host walk asks this once per
    /// file and the matcher normalises whatever it is handed, so an un-normalised list would be
    /// re-parsed once per file per pattern — which is what "built once" was claiming not to do.
    /// </para>
    /// </remarks>
    private sealed class PathSet(IReadOnlyList<string> paths)
    {
        private readonly IReadOnlyList<string> _paths = [.. paths.Select(PathPatterns.Normalize)];

        // The harness's own directory is decided in code on this side as on the sending one, so a
        // file the source would carry is one this side sees and can remove when the source no
        // longer has it, and this machine's own state there is never listed at all.
        public bool Contains(string relativePath)
            => HarnessDirectorySync.Withholds(relativePath) || PathPatterns.Matches(_paths, relativePath);
    }
}
