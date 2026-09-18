using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>How much of a tree a run takes, because two kinds of work share one.</summary>
public enum LockScope
{
    /// <summary>
    /// The whole tree, taken by syncing it: a sync rewrites files every variant reads, so nothing
    /// may build or test while it runs.
    /// </summary>
    TreeExclusive,

    /// <summary>
    /// The tree shared with other variants, and this run's own variant exclusively: variants build
    /// side by side, each in its own build directory, but never while their sources are replaced.
    /// </summary>
    TreeShared,
}

/// <summary>Who holds a lock, in enough detail to name them and to tell whether they still exist.</summary>
/// <param name="Machine">The machine the holder runs on.</param>
/// <param name="ProcessId">The holder's process id.</param>
/// <param name="ProcessStamp">
/// What tells that process from another that inherits its id, so a recycled id is not mistaken for a
/// live holder. Holds no clock, so a clock that steps cannot turn a live holder into a dead one.
/// </param>
/// <param name="RunId">The holder's run id, which is also what a log path is scoped to.</param>
/// <param name="TakenUtc">When the lock was taken, for display; nothing is ordered by it.</param>
/// <param name="Command">What the holder is doing, so the reader knows what they are waiting for.</param>
public sealed record LockHolder(
    string Machine,
    int ProcessId,
    string? ProcessStamp,
    string RunId,
    DateTimeOffset TakenUtc,
    string Command)
{
    /// <summary>
    /// The wall-clock start time a build before this one recorded here, kept only so such a file is
    /// still readable. Nothing decides anything from it: it is exactly the value that moves when the
    /// clock steps, which is why it stopped being what identifies a process.
    /// </summary>
    /// <remarks>
    /// Declared rather than skipped so that every other unknown member can be refused. Not written
    /// by this build: an entry it records carries a stamp instead. An entry it only keeps — another
    /// machine's, which it has no business rewriting — is written back as it was read, this member
    /// among it, so that machine's own build still finds what it wrote.
    /// </remarks>
    [JsonPropertyName("processStartedUtc")]
    public DateTimeOffset? LegacyStartedUtc { get; init; }
}

/// <summary>One entry in the lock file: what is held, how much of it, and by whom.</summary>
/// <param name="Host">The host the work runs on, as the command line names it.</param>
/// <param name="Tree">The tree on that host.</param>
/// <param name="Variant">The build variant, when the entry takes one; absent for a whole tree.</param>
/// <param name="Scope">How much of the tree the entry takes.</param>
/// <param name="Holder">Who holds it.</param>
public sealed record LockEntry(string Host, string Tree, string? Variant, LockScope Scope, LockHolder Holder)
{
    /// <summary>The entry as a refusal names it.</summary>
    public string Describe()
        => $"{Holder.Machine} pid {Holder.ProcessId}, run {Holder.RunId}, since {Holder.TakenUtc:u}, "
            + $"running '{Holder.Command}'{Unstamped}";

    /// <summary>
    /// Said of an entry carrying no stamp, which is one an older build wrote. Such an entry is kept
    /// while anything at all carries its id, so it can outlive its run once that id comes back around
    /// to something else. Saying so is what tells the reader that <c>--force-lock</c> is the answer
    /// here rather than waiting for a run that finished long ago.
    /// </summary>
    private string Unstamped
        => Holder.ProcessStamp is { Length: > 0 }
            ? string.Empty
            : " (recorded by an older build, so a reused id cannot be told from it; --force-lock takes it)";
}

/// <summary>What a run asks to take.</summary>
public sealed record LockRequest
{
    /// <summary>The host the work runs on.</summary>
    public required string Host { get; init; }

    /// <summary>The tree on that host.</summary>
    public required string Tree { get; init; }

    /// <summary>The build variant, required for <see cref="LockScope.TreeShared"/>.</summary>
    public string? Variant { get; init; }

    /// <summary>How much of the tree to take.</summary>
    public required LockScope Scope { get; init; }

    /// <summary>The run taking it.</summary>
    public required RunId RunId { get; init; }

    /// <summary>What this run is doing, recorded for whoever is refused.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// Whether to take a lock held by another machine. Always a human decision: liveness cannot be
    /// checked from here, so the only alternative to asking is guessing.
    /// </summary>
    public bool Force { get; init; }
}

/// <summary>
/// The run lock, <c>.harness-config/lock.json</c>, always in the main checkout.
/// </summary>
/// <remarks>
/// In the main checkout because a worktree has its own <c>.harness-config</c>, so a per-tree lock
/// would make two runs of the same leg invisible to each other. A held lock refuses immediately and
/// names its holder: silently blocking for hours is worse than a refusal somebody can act on.
/// </remarks>
public sealed class RunLock(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity)
{
    private readonly IProcessIdentity _identity = identity;

    /// <summary>The command name this reports under.</summary>
    public const string CommandName = "lock";

    /// <summary>
    /// How long one update of the lock file waits for another process's update of it. This bounds a
    /// rewrite that takes microseconds, not a run: a lock already held is refused at once, whether
    /// or not the file could be updated immediately.
    /// </summary>
    private static readonly TimeSpan UpdateWindow = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },

        // A shape this build does not recognise is a hard failure rather than silent data loss, as
        // it is wherever this tool reads JSON that decides something: the configuration, the owner
        // file, the sync marker and the host protocol. The one field an older build wrote and this
        // one no longer uses is declared on the holder, so upgrading reads its own lock file rather
        // than refusing it.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// Takes what <paramref name="request"/> asks for, or refuses naming the holder.
    /// </summary>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="request">What to take.</param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    /// <exception cref="HarnessException">
    /// Another run holds it, or the lock file could not be read or updated. Never a wait: a run
    /// blocked for hours on a lock cannot be told from one that hung.
    /// </exception>
    public async Task<RunLockHandle> AcquireAsync(
        HarnessLayout layout,
        LockRequest request,
        CancellationToken cancellationToken = default)
    {
        var attempt = await TryAcquireAsync(layout, request, cancellationToken).ConfigureAwait(false);

        return attempt.Handle ?? throw new HarnessException(HarnessExit.Refused, attempt.HeldBy!);
    }

    /// <summary>
    /// Takes what <paramref name="request"/> asks for, or says which run holds it.
    /// </summary>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="request">What to take.</param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    /// <remarks>
    /// A lock another run holds is an answer, about one tree at one moment, and is returned as one: a
    /// caller that turns it into a verdict for the legs on that tree must not also turn into one a
    /// lock file nobody can use, which stops every run on every tree alike.
    /// </remarks>
    /// <exception cref="HarnessException">
    /// The lock file could not be read or updated, or the request names no variant for a shared
    /// hold. Never a wait: a run blocked for hours on a lock cannot be told from one that hung.
    /// </exception>
    public Task<LockAttempt> TryAcquireAsync(
        HarnessLayout layout,
        LockRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Scope == LockScope.TreeShared && string.IsNullOrWhiteSpace(request.Variant))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                "A shared lock takes the tree and one variant, and no variant was named; a whole tree is taken exclusively.");
        }

        var entry = new LockEntry(
            request.Host,
            request.Tree,
            request.Variant,
            request.Scope,
            new LockHolder(
                _identity.CurrentMachine,
                _identity.CurrentId,
                _identity.Current,
                request.RunId.Value,
                DateTimeOffset.UtcNow,
                request.Command));

        string? heldBy = null;

        try
        {
            Update(
                layout,
                entries =>
                {
                    var kept = Live(entries, entry, request.Force);

                    if (kept.FirstOrDefault(existing => Conflicts(existing, entry)) is { } holder)
                    {
                        heldBy = $"{Describe(entry)} is held by {holder.Describe()}.";

                        // Thrown through the update so that nothing is written: a request that was
                        // refused leaves the file as it found it.
                        throw new LockHeldException();
                    }

                    return [.. kept, entry];
                });
        }
        catch (LockHeldException)
        {
            return Task.FromResult(new LockAttempt(null, heldBy));
        }

        return Task.FromResult(new LockAttempt(new RunLockHandle(this, layout, entry), null));
    }

    /// <summary>What leaves an update when another run holds what was asked for, writing nothing.</summary>
    private sealed class LockHeldException : Exception;

    /// <summary>Every entry currently recorded, live or not.</summary>
    /// <param name="layout">The repository whose main checkout holds the lock file.</param>
    public IReadOnlyList<LockEntry> Read(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return ReadFile(layout.LockFile);
    }

    /// <summary>
    /// Gives up <paramref name="entry"/>, and only that entry. A lock is released by the run that
    /// took it: a run that removed another's would hand two runs the same tree.
    /// </summary>
    /// <param name="layout">The repository whose main checkout holds the lock file.</param>
    /// <param name="entry">The entry this run took.</param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    internal Task ReleaseAsync(HarnessLayout layout, LockEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Update(layout, entries => [.. entries.Where(existing => !Ours(existing, entry))]);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether <paramref name="wanted"/> cannot be taken while <paramref name="existing"/> stands.
    /// A tree taken exclusively excludes everything on it; a tree taken shared excludes only the
    /// same variant, which is what lets variants build side by side.
    /// </summary>
    private static bool Conflicts(LockEntry existing, LockEntry wanted)
    {
        if (!Same(existing.Host, wanted.Host) || !Same(existing.Tree, wanted.Tree))
        {
            return false;
        }

        return existing.Scope == LockScope.TreeExclusive
            || wanted.Scope == LockScope.TreeExclusive
            || Same(existing.Variant, wanted.Variant);
    }

    private static bool Ours(LockEntry existing, LockEntry ours)
        => Same(existing.Host, ours.Host)
            && Same(existing.Tree, ours.Tree)
            && Same(existing.Variant, ours.Variant)
            && existing.Holder.ProcessId == ours.Holder.ProcessId
            && string.Equals(existing.Holder.RunId, ours.Holder.RunId, StringComparison.Ordinal)
            && string.Equals(existing.Holder.Machine, ours.Holder.Machine, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static string Describe(LockEntry entry)
        => entry.Variant is { Length: > 0 } variant
            ? $"'{entry.Tree}' variant '{variant}' on {entry.Host}"
            : $"'{entry.Tree}' on {entry.Host}";

    /// <summary>
    /// The entries still standing: a dead holder on this machine is reclaimed and the reclaim is
    /// reported, and a holder on another machine stands until <c>--force-lock</c> says otherwise,
    /// because nothing here can ask that machine whether it is still running.
    /// </summary>
    private IReadOnlyList<LockEntry> Live(IReadOnlyList<LockEntry> entries, LockEntry wanted, bool force)
    {
        var kept = new List<LockEntry>();

        foreach (var entry in entries)
        {
            // Only the entry actually in the way. --force-lock says "this lock is stale, take it";
            // taking every other lock as well would drop holds on trees and variants this run never
            // asked for, including one another run is mid-sync on.
            //
            // Asked before the holder's machine is, so that a lock this machine will not reclaim on
            // its own can still be given up. Otherwise an entry whose process id has come back around
            // to something live is held for ever, and only editing the file by hand recovers it.
            if (force && Conflicts(entry, wanted))
            {
                _output.Warn(CommandName, $"Taking {Describe(entry)} from {entry.Describe()} because --force-lock was given.");
                continue;
            }

            var mine = string.Equals(entry.Holder.Machine, _identity.CurrentMachine, StringComparison.OrdinalIgnoreCase);

            if (!mine)
            {
                kept.Add(entry);
                continue;
            }

            if (_identity.IsAlive(entry.Holder.ProcessId, entry.Holder.ProcessStamp))
            {
                kept.Add(entry);
                continue;
            }

            // Reported rather than done quietly: a lock that disappears without a word is
            // indistinguishable from one that was never taken.
            _output.Info(CommandName, $"Reclaimed {Describe(entry)} from {entry.Describe()}, which is no longer running.");
        }

        return kept;
    }

    /// <summary>
    /// Reads the lock file, applies <paramref name="change"/> and writes it back, with no other
    /// process in between.
    /// </summary>
    /// <remarks>
    /// The read and the write are one step because two runs that each read "free" would each write
    /// themselves in, and both would believe they held the tree. A machine-wide named mutex is what
    /// makes them one step — the same mechanism the anchor registries use, and the only one .NET
    /// offers on Windows, Linux and macOS alike — and it is held for the length of a rewrite, not
    /// for the length of a run. The work inside it is synchronous on purpose: a mutex is released
    /// by the thread that took it, and an await could resume on another.
    /// </remarks>
    private void Update(HarnessLayout layout, Func<IReadOnlyList<LockEntry>, IReadOnlyList<LockEntry>> change)
    {
        var path = Path.GetFullPath(layout.LockFile);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(path)!);

        MachineWideFile.Update<object?>(path, UpdateWindow, () =>
        {
            var entries = change(ReadFile(path));

            // Written through the atomic write, which renames a complete file over the old one and
            // retries a sharing violation: a reader never sees a half-written lock file, and a
            // crash never truncates one into a file that appears to hold nothing.
            _fileSystem.WriteAllTextAtomic(path, JsonSerializer.Serialize(entries, JsonOptions) + "\n");
            return null;
        });
    }

    private IReadOnlyList<LockEntry> ReadFile(string path)
    {
        if (!_fileSystem.FileExists(path))
        {
            return [];
        }

        string text;

        try
        {
            text = _fileSystem.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"The run lock '{path}' could not be read: {ex.Message}. Until it can be, one run cannot be told from another.",
                ex);
        }

        try
        {
            return JsonSerializer.Deserialize<List<LockEntry>>(text, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // Never read as "nothing is held": a lock file that cannot be parsed is exactly the
            // case where two runs would otherwise both proceed.
            throw new HarnessException(
                HarnessExit.Refused,
                $"The run lock '{path}' is not readable as JSON: {ex.Message}. Remove it once no run is using it.",
                ex);
        }
    }
}

/// <summary>
/// What a run holds, and the only thing that can give it up. Released by the run that took it, and
/// by nothing else: a release that matched on the tree alone would hand a second run a tree the
/// first was still building in.
/// </summary>
/// <summary>What asking for a lock came to: the lock, or the run that holds it. Exactly one is set.</summary>
/// <param name="Handle">The lock, when it was taken.</param>
/// <param name="HeldBy">Which run holds it, said as a refusal says it, when it was not.</param>
public sealed record LockAttempt(RunLockHandle? Handle, string? HeldBy);

public sealed class RunLockHandle : IAsyncDisposable
{
    private readonly RunLock _lock;
    private readonly HarnessLayout _layout;
    private bool _released;

    internal RunLockHandle(RunLock runLock, HarnessLayout layout, LockEntry entry)
    {
        _lock = runLock;
        _layout = layout;
        Entry = entry;
    }

    /// <summary>The entry this run took.</summary>
    public LockEntry Entry { get; }

    /// <summary>Gives up the lock. Calling it twice is not an error; the second call does nothing.</summary>
    /// <param name="cancellationToken">Stops the attempt.</param>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_released)
        {
            return;
        }

        _released = true;
        await _lock.ReleaseAsync(_layout, Entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives up the lock when the run leaves the block that took it.</summary>
    public async ValueTask DisposeAsync() => await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
}
