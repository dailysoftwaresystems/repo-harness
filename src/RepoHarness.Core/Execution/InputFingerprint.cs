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
    public ReachedVerdict? Verdict() => Change switch
    {
        InputChange.Moved => ReachedVerdict.Of(LegVerdict.InputsMoved, Detail),
        InputChange.Unmeasured => ReachedVerdict.Of(LegVerdict.Unmeasured, Detail),
        _ => null,
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
    /// object, so that an edit made and undone while the suite ran is still caught.
    /// </summary>
    /// <param name="root">The tree the paths are relative to.</param>
    /// <param name="inputs">The inputs, relative to the tree.</param>
    public InputWatch Watch(string root, IReadOnlyList<string> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(inputs);

        return new InputWatch(root, inputs.Select(Normalize), Comparer);
    }

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

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _tracked;
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly FileSystemWatcher? _watcher;
    private string? _failure;

    internal InputWatch(string root, IEnumerable<string> inputs, StringComparer comparer)
    {
        _root = Path.GetFullPath(root);
        _tracked = new Dictionary<string, string>(comparer);

        foreach (var input in inputs)
        {
            _tracked[input] = input;
        }

        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = BufferBytes,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };

            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A machine that cannot watch this tree — one out of inotify watches, say — leaves the
            // question open. It never leaves it answered "nothing changed".
            _failure = ex.Message;
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

    /// <summary>Stops watching. The changes already recorded stay readable.</summary>
    public void Dispose()
    {
        try
        {
            _watcher?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already gone, or the platform tore the watch down first; either way nothing is lost,
            // because what it recorded is held here rather than in the watcher.
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
