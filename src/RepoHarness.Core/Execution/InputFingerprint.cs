using System.Security.Cryptography;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

/// <summary>One input file as it stood at one moment.</summary>
/// <param name="Path">The file, relative to the tree and always spelled with forward slashes.</param>
/// <param name="Length">Its size in bytes, or <see cref="InputFingerprint.AbsentLength"/> when it was not there.</param>
/// <param name="Content">A hash of its bytes, or <see cref="InputFingerprint.AbsentContent"/> when it was not there.</param>
/// <remarks>
/// Content and size, never a timestamp. Change is detected by equality, which a clock cannot
/// distort because both readings carry the same distortion; one host's wall clock steps forward by
/// about 25 seconds every few seconds, and those steps reach file modification times.
/// </remarks>
public sealed record FileFingerprint(string Path, long Length, string Content);

/// <summary>An input file that could not be fingerprinted, and why.</summary>
/// <param name="Path">The file, relative to the tree.</param>
/// <param name="Reason">What stopped the reading.</param>
public sealed record UnreadableInput(string Path, string Reason);

/// <summary>
/// Every input as it stood at one moment, together with whatever could not be read. The two are
/// kept apart so that an unreadable snapshot can never be compared as though it were complete.
/// </summary>
/// <param name="Files">Each input's content and size.</param>
/// <param name="Unreadable">Inputs whose bytes could not be read at all.</param>
public sealed record InputSnapshot(IReadOnlyList<FileFingerprint> Files, IReadOnlyList<UnreadableInput> Unreadable)
{
    /// <summary>Whether every declared input was measured. A snapshot that was not is never clean.</summary>
    public bool Complete => Unreadable.Count == 0;
}

/// <summary>What comparing two snapshots established.</summary>
public enum InputChange
{
    /// <summary>Every input held still for the whole phase.</summary>
    Unchanged,

    /// <summary>At least one input changed while the phase ran.</summary>
    Moved,

    /// <summary>Whether they held still could not be established.</summary>
    Unmeasured,
}

/// <summary>What the before and after snapshots, and the watch between them, said together.</summary>
/// <param name="Change">Whether the inputs held still, moved, or could not be measured.</param>
/// <param name="Changed">The inputs that moved, or the ones that could not be measured.</param>
/// <param name="Detail">The sentence the ledger shows.</param>
public sealed record InputComparison(InputChange Change, IReadOnlyList<string> Changed, string Detail)
{
    /// <summary>
    /// The verdict this forces on the leg, or <see langword="null"/> when the inputs held still.
    /// A leg whose inputs moved is not reported as failed even if its tests failed, because what
    /// failed was a tree that never existed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A change this build does not know how to weigh. Raised rather than absorbed by a default
    /// arm: this is the one type whose whole purpose is that an unestablished measurement never
    /// reads as clean, and a silent <see langword="null"/> for a case added later is exactly that.
    /// </exception>
    public ReachedVerdict? Verdict() => Change switch
    {
        InputChange.Unchanged => null,
        InputChange.Moved => ReachedVerdict.Of(LegVerdict.InputsMoved, Detail),
        InputChange.Unmeasured => ReachedVerdict.Of(LegVerdict.Unmeasured, Detail),
        _ => throw new ArgumentOutOfRangeException(nameof(Change), Change, "This build cannot weigh that input change."),
    };
}

/// <summary>
/// Fingerprints the files a leg's tests read, before they start and after they end, and watches
/// them while they run.
/// </summary>
/// <remarks>
/// Edit a file mid-run and some tests see the old one and some the new, and the report describes a
/// tree that never existed. This was measured: eight failures, all passing seconds later on the
/// unchanged tree, because a configuration file was rewritten while the suite ran. The watch exists
/// because two snapshots alone cannot see an edit that was reverted before the suite finished.
/// </remarks>
public sealed class InputFingerprint(IFileSystem fileSystem, IHostPlatform platform)
{
    /// <summary>The size recorded for an input that was not there.</summary>
    public const long AbsentLength = -1;

    /// <summary>
    /// The content recorded for an input that was not there. Absence is a measurement, and it
    /// compares equal to absence: a file missing before and after did not move. It is unreadability,
    /// not absence, that leaves the question open.
    /// </summary>
    public const string AbsentContent = "absent";

    /// <summary>How many changed inputs the ledger names before it counts the rest.</summary>
    private const int NamedInDetail = 3;

    /// <summary>
    /// How long a watch on macOS is given to deliver changes made before it began. Generous next to
    /// the milliseconds such a change takes to arrive: the one this answers was made moments before
    /// its watch started.
    /// </summary>
    private static readonly TimeSpan MacOsLateDelivery = TimeSpan.FromMilliseconds(250);

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <summary>
    /// Fingerprints <paramref name="inputs"/> under <paramref name="root"/> by content and size, on
    /// the host that runs them.
    /// </summary>
    /// <param name="root">The tree the paths are relative to.</param>
    /// <param name="inputs">The inputs, relative to the tree; the caller resolves whatever globbed them.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<InputSnapshot> TakeAsync(
        string root,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(inputs);

        var files = new List<FileFingerprint>();
        var unreadable = new List<UnreadableInput>();

        foreach (var input in inputs.Distinct(Comparer).OrderBy(input => input, Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Normalize(input);
            var full = Path.Combine(root, input);

            if (!_fileSystem.FileExists(full))
            {
                files.Add(new FileFingerprint(relative, AbsentLength, AbsentContent));
                continue;
            }

            try
            {
                files.Add(await FingerprintAsync(relative, full, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Recorded, never passed over. An input that could not be read is what makes the
                // whole snapshot unusable, and a snapshot short of one file would otherwise compare
                // equal to another snapshot short of the same file.
                unreadable.Add(new UnreadableInput(relative, ex.Message));
            }
        }

        return new InputSnapshot(files, unreadable);
    }

    /// <summary>
    /// Watches <paramref name="inputs"/> under <paramref name="root"/> for the life of the returned
    /// object, so that an edit made and undone while the suite ran is still caught; returned once
    /// what it reports is what happened after it began.
    /// </summary>
    /// <param name="root">The tree the paths are relative to.</param>
    /// <param name="inputs">The inputs, relative to the tree.</param>
    /// <param name="cancellationToken">Stops the wait for what the platform may still deliver.</param>
    /// <remarks>
    /// Asked for before the work it watches starts, which is what lets it disregard what this platform
    /// delivers late: see <see cref="InputWatch.SettleAsync"/>.
    /// </remarks>
    public async Task<InputWatch> WatchAsync(
        string root,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(inputs);

        var watch = new InputWatch(root, inputs.Select(Normalize), Comparer);

        try
        {
            await watch.SettleAsync(LateDelivery, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            watch.Dispose();
            throw;
        }

        return watch;
    }

    /// <summary>
    /// How long a watch on this platform is given to deliver changes made before it began, which it
    /// then disregards; zero where a watch reports only what happens once it exists.
    /// </summary>
    /// <remarks>
    /// macOS delivers file events through a service that numbers each one as it reads it, and a watch
    /// takes the events numbered after the one current when it started. A change made a moment before is
    /// sometimes numbered after, and delivered to a watch that did not exist when it was made. Counted,
    /// it reads as an input moving under work that had not begun: on a CI run, a test leg whose fixture
    /// was written just before it ran was reported as having its inputs move. Linux's and Windows'
    /// watches report only what happens once they exist, and nothing is waited for there.
    /// <para>
    /// A bound, not a proof: nothing the watch exposes says the platform has caught up. A change
    /// delivered later than this still reads as moved, which is the side a guard errs on.
    /// </para>
    /// </remarks>
    internal TimeSpan LateDelivery => _platform.Current == PlatformId.MacOs ? MacOsLateDelivery : TimeSpan.Zero;

    /// <summary>
    /// What <paramref name="before"/>, <paramref name="after"/> and <paramref name="watch"/> say
    /// together. An incomplete snapshot, or a watch that lost events, yields <c>unmeasured</c>:
    /// there is no escape hatch, because a command that rewrites its own inputs is a build step,
    /// not a test.
    /// </summary>
    /// <param name="before">The snapshot taken before the phase.</param>
    /// <param name="after">The snapshot taken after it.</param>
    /// <param name="watch">The watch kept while it ran, when one was kept.</param>
    public static InputComparison Compare(InputSnapshot before, InputSnapshot after, InputWatch? watch = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var unmeasured = before.Unreadable
            .Concat(after.Unreadable)
            .Select(entry => entry.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (unmeasured.Count > 0)
        {
            return new InputComparison(
                InputChange.Unmeasured,
                unmeasured,
                $"{Describe(unmeasured.Count, "input")} could not be read: {List(unmeasured)}");
        }

        if (watch?.Failure is { } failure)
        {
            return new InputComparison(
                InputChange.Unmeasured,
                [],
                $"the inputs could not be watched while the phase ran: {failure}");
        }

        var moved = Differences(before, after)
            .Concat(watch?.Changed ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return moved.Count == 0
            ? new InputComparison(InputChange.Unchanged, [], string.Empty)
            : new InputComparison(
                InputChange.Moved,
                moved,
                $"{Describe(moved.Count, "input")} changed: {List(moved)}");
    }

    private static IEnumerable<string> Differences(InputSnapshot before, InputSnapshot after)
    {
        var start = before.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var end = after.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);

        foreach (var (path, first) in start)
        {
            // A file the second snapshot never looked at is a change in what was measured, not
            // evidence that nothing happened to it.
            if (!end.TryGetValue(path, out var last)
                || last.Length != first.Length
                || !string.Equals(last.Content, first.Content, StringComparison.Ordinal))
            {
                yield return path;
            }
        }

        foreach (var path in end.Keys.Where(path => !start.ContainsKey(path)))
        {
            yield return path;
        }
    }

    private async Task<FileFingerprint> FingerprintAsync(string relative, string full, CancellationToken cancellationToken)
    {
        // The same hash a sync compares trees by, so "the same file" means one thing throughout: a
        // second implementation would eventually answer differently about one file, and the two
        // places that ask — what to transfer, and whether the inputs held still — would disagree.
        var content = await FileContentHash.OfAsync(_fileSystem, full, cancellationToken).ConfigureAwait(false);

        return new FileFingerprint(relative, content.Length, content.Content);
    }

    private static string Describe(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string List(IReadOnlyList<string> paths)
        => paths.Count <= NamedInDetail
            ? string.Join(", ", paths)
            : string.Join(", ", paths.Take(NamedInDetail)) + $", and {paths.Count - NamedInDetail} more";

    /// <summary>Forward slashes everywhere, so one input is one name in the report on every platform.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>How input paths compare: as this platform compares paths, since that is what decides whether two names are one file.</summary>
    private StringComparer Comparer
        => _platform.PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// Watches the declared inputs for the life of this object and records every one that changed.
/// </summary>
/// <remarks>
/// Two snapshots cannot see an edit that was undone before the second one was taken, and that is
/// the shape the measured failure took: a configuration file rewritten while the suite ran and
/// restored before it ended. What the watch could not see is reported as <c>unmeasured</c> rather
/// than as nothing found, for the reason an unreadable snapshot is.
/// </remarks>
public sealed class InputWatch : IDisposable
{
    /// <summary>
    /// The largest buffer the operating system accepts. Events arrive faster than they can be read
    /// during a build, and a buffer that overflows loses them silently, which this exists to avoid.
    /// </summary>
    private const int BufferBytes = 64 * 1024;

    /// <summary>
    /// How many directories are watched separately before one watch over the whole tree is taken
    /// instead. A tree with more top-level directories than this is not the shape this optimisation
    /// is for, and a watch per directory would spend handles to no purpose.
    /// </summary>
    private const int MostDirectoriesWatched = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _tracked;
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly List<FileSystemWatcher> _watchers = [];
    private string? _failure;

    internal InputWatch(string root, IEnumerable<string> inputs, StringComparer comparer)
    {
        _root = Path.GetFullPath(root);
        _tracked = new Dictionary<string, string>(comparer);

        foreach (var input in inputs)
        {
            _tracked[input] = input;
        }

        if (!Directory.Exists(_root))
        {
            // Nothing to watch and nothing watched. Left unsaid this would answer every question
            // about the span with "nothing changed", which is the one answer it has not earned.
            _failure = $"'{_root}' is not a directory, so nothing about it could be watched";
            return;
        }

        try
        {
            foreach (var (directory, recursive) in Targets())
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = recursive,
                    InternalBufferSize = BufferBytes,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                };

                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A machine that cannot watch this tree — one out of inotify watches, say — leaves the
            // question open. It never leaves it answered "nothing changed".
            _failure = ex.Message;
        }

        if (_watchers.Count == 0)
        {
            _failure ??= $"no directory holding any of the {_tracked.Count} input(s) is present under "
                + $"'{_root}', so nothing about them could be watched";
        }
    }

    /// <summary>
    /// The directories to watch, and whether each is watched all the way down: one per top-level
    /// directory that holds a tracked input, plus the root itself for the files directly in it.
    /// </summary>
    /// <remarks>
    /// Not one watch over the whole tree. A build writes its objects into <c>build/&lt;variant&gt;</c>,
    /// which is inside the tree and is not tracked, so a recursive watch on the root receives every
    /// object file, dependency file and generated header the build emits. That overflows the
    /// operating system's buffer, and an overflow is reported as a watch that cannot be trusted —
    /// so the build that triggered it reports <c>unmeasured</c>, marks its own directory
    /// untrustworthy, and the next build starts from clean. A guard that turns a working build into
    /// a permanent full rebuild is worse than the one it replaced.
    /// <para>
    /// Watching only where the inputs are excludes the build directory, <c>.git</c> and every other
    /// untracked directory by construction, rather than by a list of names that would have to be
    /// kept correct.
    /// </para>
    /// </remarks>
    private IEnumerable<(string Directory, bool Recursive)> Targets()
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var rootFiles = false;

        foreach (var input in _tracked.Keys)
        {
            var at = input.IndexOf('/', StringComparison.Ordinal);

            if (at <= 0)
            {
                rootFiles = true;
                continue;
            }

            directories.Add(input[..at]);
        }

        if (directories.Count > MostDirectoriesWatched)
        {
            yield return (_root, true);
            yield break;
        }

        if (rootFiles)
        {
            // Not recursive: the files directly in the root, and nothing under a directory that has
            // its own watch or no tracked input at all.
            yield return (_root, false);
        }

        foreach (var directory in directories.Order(StringComparer.Ordinal))
        {
            var full = Path.Combine(_root, directory);

            // A directory that is not there cannot be watched. Its inputs are absent, which the
            // fingerprints on either side of the work report on their own.
            if (Directory.Exists(full))
            {
                yield return (full, true);
            }
        }
    }

    /// <summary>The inputs seen changing, in the spelling the configuration declared them in.</summary>
    public IReadOnlyList<string> Changed
    {
        get
        {
            lock (_gate)
            {
                return [.. _changed.OrderBy(path => path, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Why the watch cannot be trusted, or <see langword="null"/> when it can.</summary>
    public string? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>
    /// Waits <paramref name="lateDelivery"/> for what the platform may still deliver about changes
    /// made before the watch began, then disregards every change it has seen, so that what it reports
    /// from then on happened while it watched.
    /// </summary>
    /// <param name="lateDelivery">How long to wait; at zero nothing is waited for or disregarded.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <remarks>
    /// Sound only before the work it watches starts, which is the one place it is asked. A change
    /// disregarded here was made before the work began: made before the first snapshot, that snapshot
    /// holds it, and it is what the work read; made after it and left in place, the snapshot taken once
    /// the work ends differs from the first; undone again, it was never read. A watch that could not be
    /// trusted stays so.
    /// </remarks>
    internal async Task SettleAsync(TimeSpan lateDelivery, CancellationToken cancellationToken)
    {
        if (lateDelivery <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(lateDelivery, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _changed.Clear();
        }
    }

    /// <summary>Stops watching. The changes already recorded stay readable.</summary>
    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already gone, or the platform tore the watch down first; either way nothing is
                // lost, because what it recorded is held here rather than in the watcher.
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Record(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Both names, because a rename is a change to the file that left and to the one that arrived.
        Record(e.OldFullPath);
        Record(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_gate)
        {
            _failure ??= e.GetException().Message;
        }
    }

    private void Record(string fullPath)
    {
        string relative;

        try
        {
            relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return;
        }

        lock (_gate)
        {
            if (_tracked.TryGetValue(relative, out var declared))
            {
                _changed.Add(declared);
            }
        }
    }
}
