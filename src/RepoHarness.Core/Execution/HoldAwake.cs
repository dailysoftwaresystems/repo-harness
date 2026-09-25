using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Execution;

/// <summary>The hold that keeps this machine awake between commands, as the machine held keeps it.</summary>
/// <param name="Generation">Which hold this is: a newer one, or its end, changes it, and the hold's process stops.</param>
/// <param name="Until">When it ends by itself.</param>
/// <param name="KeepAwake">The machine's <c>keepAwake</c> command, as the machine that asked for the hold declares it.</param>
/// <param name="Environment">What the machine declares under <c>env</c>, which the command starts under.</param>
/// <param name="ProgramDirectories">Where the survey found the machine's programs, which the command is looked for in.</param>
public sealed record HoldAwakeState(
    string Generation,
    DateTimeOffset Until,
    List<string> KeepAwake,
    Dictionary<string, string> Environment,
    List<string> ProgramDirectories);

/// <summary>
/// Where the hold that keeps this machine awake between commands is kept - one per user of the machine,
/// whichever repositories and worktrees reach it - and how it is ended.
/// </summary>
/// <remarks>
/// The hold's process watches its state, and stops when a newer hold replaces it or the state is gone: no
/// process is ever stopped by an id recorded somewhere, which a process started since could have been given.
/// Kept among this user's own application data, never in a directory other users can write.
/// </remarks>
/// <param name="fileSystem">Reads and writes the state.</param>
/// <param name="path">The file the state is kept in.</param>
public sealed class HoldAwakeStore(IFileSystem fileSystem, string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Where a hold is kept for the user running this process.</summary>
    public static string DefaultPath => Path.Combine(ApplicationData(), "dssharness", "hold-awake.json");

    /// <summary>The file the state is kept in.</summary>
    public string Location { get; } = path;

    /// <summary>This user's own application data directory, below the home directory this process was given.</summary>
    /// <remarks>
    /// macOS's lookup of it asks the system for the account's home rather than reading <c>HOME</c>, which every
    /// other path a process there derives honors - and which a process given another home, as a test gives it,
    /// then does not reach. Its place below that home is the same.
    /// </remarks>
    private static string ApplicationData()
    {
        if (OperatingSystem.IsMacOS()
            && Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } home)
        {
            return Path.Combine(home, "Library", "Application Support");
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } own
            ? own
            : Path.GetTempPath();
    }

    /// <summary>Makes <paramref name="state"/> the hold that stands, ending any before it.</summary>
    /// <param name="state">The hold.</param>
    /// <exception cref="IOException">The state could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The state's directory may not be written.</exception>
    public void Write(HoldAwakeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _fileSystem.WriteAllTextAtomic(Location, JsonSerializer.Serialize(state, JsonOptions));
    }

    /// <summary>The hold that stands, or <see langword="null"/> where none does, or its state cannot be read.</summary>
    public HoldAwakeState? Read()
    {
        try
        {
            return _fileSystem.FileExists(Location)
                ? JsonSerializer.Deserialize<HoldAwakeState>(_fileSystem.ReadAllText(Location), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ends the hold that stands, if one does: its process sees its state gone and stops. Removing a file
    /// needs no room, so this is done even on a full disk; one that cannot be removed leaves the hold to end
    /// by itself.
    /// </summary>
    public void End()
    {
        try
        {
            _fileSystem.DeleteFile(Location);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Said nowhere: a hold left standing ends when its time does, and whatever is starting now holds the
            // machine awake itself.
        }
    }
}

/// <summary>
/// Holds this machine awake between commands, for the hold it was started for: what the hidden <c>host-hold</c>
/// runs, detached from the connection that asked for it.
/// </summary>
/// <param name="store">Where the hold is kept.</param>
/// <param name="keepAwake">Starts the machine's own <c>keepAwake</c>, filled in with this process.</param>
/// <param name="clock">What measures the hold.</param>
/// <param name="pollInterval">How often the hold's state is read again; production uses <see cref="PollInterval"/>.</param>
public sealed class HoldAwakeService(HoldAwakeStore store, KeepAwake keepAwake, TimeProvider clock, TimeSpan pollInterval)
{
    /// <summary>The hidden command that runs a hold, which a host is never asked to run through a request.</summary>
    public const string CommandName = "host-hold";

    /// <summary>
    /// How often a hold reads its state again: how long, at most, the machine stays held once a command's own
    /// keepAwake has taken over, or a newer hold has replaced this one.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly HoldAwakeStore _store = store;
    private readonly KeepAwake _keepAwake = keepAwake;
    private readonly TimeProvider _clock = clock;
    private readonly TimeSpan _pollInterval = pollInterval;

    /// <summary>
    /// Holds the machine awake with its <c>keepAwake</c> while the hold <paramref name="generation"/> stands:
    /// until its time is up, a newer hold replaces it, or a command's own keepAwake ends it.
    /// </summary>
    /// <param name="generation">The hold this process was started for.</param>
    /// <param name="cancellationToken">Stops the hold.</param>
    public async Task RunAsync(string generation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);

        if (_store.Read() is not { KeepAwake.Count: > 0 } state || state.Generation != generation)
        {
            return;
        }

        var host = new LocalHostConfig
        {
            KeepAwake = [.. state.KeepAwake],
            Env = new Dictionary<string, string>(state.Environment, StringComparer.OrdinalIgnoreCase),
        };

        // Filled in with this process, which ends with the hold: a command such as caffeinate -w stops with it,
        // however it ends. Started without ending the hold that stands, which is this one.
        await using (_keepAwake.Hold(CommandName, "between commands", host, state.ProgramDirectories, cancellationToken, endsHold: false))
        {
            while (_clock.GetUtcNow() < state.Until && _store.Read()?.Generation == generation)
            {
                var left = state.Until - _clock.GetUtcNow();

                try
                {
                    await Task.Delay(left < _pollInterval ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : _pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
