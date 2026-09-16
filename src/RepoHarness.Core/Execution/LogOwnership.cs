using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>The run that owns a log directory.</summary>
/// <param name="Machine">The machine it runs on.</param>
/// <param name="ProcessId">Its process id.</param>
/// <param name="ProcessStamp">
/// What tells that process from another that inherits its id, so a recycled id is not read as a live
/// owner. Holds no clock, so a clock that steps cannot turn a live owner into a dead one.
/// </param>
/// <param name="RunId">Its run id.</param>
/// <param name="TakenUtc">When it claimed the directory, for display.</param>
public sealed record LogOwner(
    string Machine,
    int ProcessId,
    string? ProcessStamp,
    string RunId,
    DateTimeOffset TakenUtc)
{
    /// <summary>The owner as a refusal names it.</summary>
    public string Describe()
        => $"{Machine} pid {ProcessId}, run {RunId}, since {TakenUtc:u}{Unstamped}";

    /// <summary>
    /// Said of an owner carrying no stamp, which is one an older build wrote. Kept while anything at
    /// all carries its id, so it can outlive its run once that id comes back around to something else.
    /// </summary>
    private string Unstamped
        => ProcessStamp is { Length: > 0 }
            ? string.Empty
            : " (recorded by an older build, so a reused id cannot be told from it; --force-lock takes it)";
}

/// <summary>What claiming a log directory found.</summary>
/// <param name="Taken">Whether this run now owns it.</param>
/// <param name="Holder">The live run that owns it instead, when one does.</param>
/// <param name="OwnerFile">Where the ownership is recorded.</param>
public sealed record LogClaim(bool Taken, LogOwner? Holder, string OwnerFile)
{
    /// <summary>
    /// The verdict this forces on the leg, or <see langword="null"/> when the claim succeeded. A run
    /// that cannot own its log path cannot keep the evidence for its own verdict, and a verdict with
    /// no evidence behind it is the thing this tool exists to stop reporting.
    /// </summary>
    public ReachedVerdict? Verdict()
        => Taken
            ? null
            : ReachedVerdict.Of(
                LegVerdict.LogHeld,
                Holder is null ? "another run owns this log path" : $"another run owns this log path: {Holder.Describe()}");
}

/// <summary>
/// Records which run owns a log directory, so that two runs cannot write one set of logs.
/// </summary>
/// <remarks>
/// Every run has its own id and every log is scoped to it, which keeps two runs apart as long as
/// both chose their own id. A run told to write somewhere already owned — a rerun pointed at a
/// previous run's directory, or two runs given the same one — is refused as <c>log-held</c> rather
/// than allowed to interleave its output with another run's, since one leg's result read as
/// another's is exactly the failure the ids exist to prevent.
/// </remarks>
public sealed class LogOwnership(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity)
{
    private readonly IProcessIdentity _identity = identity;

    /// <summary>The command name this reports under.</summary>
    public const string CommandName = "logs";

    /// <summary>
    /// The suffix of the file recording the owner. Beside the log directory rather than inside it,
    /// so that wiping a run directory cannot quietly free a directory a live run still owns.
    /// </summary>
    public const string OwnerSuffix = ".owner.json";

    /// <summary>How long one update of the owner file waits for another process's; see <see cref="MachineWideFile"/>.</summary>
    private static readonly TimeSpan UpdateWindow = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>Where a log directory's ownership is recorded.</summary>
    /// <param name="logDirectory">The directory a run writes its logs to.</param>
    public static string OwnerFile(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(logDirectory)) + OwnerSuffix;
    }

    /// <summary>
    /// Claims <paramref name="logDirectory"/> for <paramref name="runId"/>. An owner whose process
    /// has gone is reclaimed and the reclaim is reported; a live one is refused, and the leg's
    /// verdict is <c>log-held</c>.
    /// </summary>
    /// <param name="logDirectory">The directory this run writes its logs to.</param>
    /// <param name="runId">The run claiming it.</param>
    /// <param name="force">
    /// Whether to take a path a live owner still holds, which <c>--force-lock</c> asks for. The only
    /// way out of an owner that cannot be reclaimed: without it the file has to be deleted by hand.
    /// </param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    public Task<LogClaim> ClaimAsync(
        string logDirectory,
        RunId runId,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(runId);
        cancellationToken.ThrowIfCancellationRequested();

        var file = OwnerFile(logDirectory);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(file)!);

        var claim = MachineWideFile.Update(file, UpdateWindow, () =>
        {
            if (Read(file) is { } existing && !Mine(existing, runId))
            {
                var held = _identity.IsAlive(existing.ProcessId, existing.ProcessStamp)
                    || !string.Equals(existing.Machine, _identity.CurrentMachine, StringComparison.OrdinalIgnoreCase);

                if (held && !force)
                {
                    // A holder on another machine cannot be asked whether it is still running, so
                    // it stands. Liveness, never a timeout, is what decides for one on this machine.
                    return new LogClaim(false, existing, file);
                }

                if (held)
                {
                    _output.Warn(CommandName, $"Taking the log path '{logDirectory}' from {existing.Describe()} because --force-lock was given.");
                }
                else
                {
                    _output.Info(CommandName, $"Reclaimed the log path '{logDirectory}' from {existing.Describe()}, which is no longer running.");
                }
            }

            var owner = new LogOwner(
                _identity.CurrentMachine,
                _identity.CurrentId,
                _identity.Current,
                runId.Value,
                DateTimeOffset.UtcNow);

            _fileSystem.WriteAllTextAtomic(file, JsonSerializer.Serialize(owner, JsonOptions) + "\n");
            return new LogClaim(true, owner, file);
        });

        return Task.FromResult(claim);
    }

    /// <summary>
    /// Gives up <paramref name="logDirectory"/>, and only when this run owns it. A release that did
    /// not check would free a directory another run had just claimed.
    /// </summary>
    /// <param name="logDirectory">The directory this run wrote its logs to.</param>
    /// <param name="runId">The run giving it up.</param>
    /// <param name="cancellationToken">Stops the attempt.</param>
    public Task ReleaseAsync(string logDirectory, RunId runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(runId);
        cancellationToken.ThrowIfCancellationRequested();

        var file = OwnerFile(logDirectory);

        MachineWideFile.Update<object?>(file, UpdateWindow, () =>
        {
            if (Read(file) is { } existing && Mine(existing, runId))
            {
                _fileSystem.DeleteFile(file);
            }

            return null;
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// The run that owns <paramref name="logDirectory"/>, or <see langword="null"/> when none does.
    /// </summary>
    /// <param name="logDirectory">The directory a run writes its logs to.</param>
    public LogOwner? Owner(string logDirectory) => Read(OwnerFile(logDirectory));

    private bool Mine(LogOwner owner, RunId runId)
        => string.Equals(owner.RunId, runId.Value, StringComparison.Ordinal)
            && owner.ProcessId == _identity.CurrentId
            && string.Equals(owner.Machine, _identity.CurrentMachine, StringComparison.OrdinalIgnoreCase);

    private LogOwner? Read(string file)
    {
        if (!_fileSystem.FileExists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LogOwner>(_fileSystem.ReadAllText(file), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An owner file that cannot be read says a run claimed this path and nothing more, so
            // the claim is refused rather than granted. Never read as free: that is the one reading
            // that lets two runs write one set of logs.
            throw new HarnessException(
                HarnessExit.Refused,
                $"The log owner file '{file}' could not be read: {ex.Message}. Remove it once no run is using that path.",
                ex);
        }
    }
}
