using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// What looking for a program on a host established. "Not found" and "could not look" are separate
/// because a refusal that says a tool is not installed sends somebody to install one that is already
/// there, and the remedies differ.
/// </summary>
public enum ProgramFound
{
    /// <summary>The host was never asked about it.</summary>
    Unknown,

    /// <summary>The PATH of a command run without a login shell names it, so its bare name starts it.</summary>
    OnPath,

    /// <summary>It is installed, in a directory that PATH does not name, so only its absolute path starts it.</summary>
    OffPath,

    /// <summary>The host answered, and it is neither on that PATH nor in any directory that was searched.</summary>
    Nowhere,

    /// <summary>
    /// The host could not be asked, or could not look everywhere it was told to, so nothing is known
    /// and no refusal may claim it is missing.
    /// </summary>
    Unreadable,
}

/// <summary>Where one program is on one host.</summary>
/// <param name="Program">The name it was asked for.</param>
/// <param name="Found">What the search established.</param>
/// <param name="Path">Where it is, when it was found; <see langword="null"/> otherwise.</param>
/// <param name="Reason">
/// Why nothing could be established, when the search could not look everywhere it was told to: a
/// directory it could not name, or a shell that gave it no way to look.
/// </param>
public sealed record ProgramLocation(string Program, ProgramFound Found, string? Path = null, string? Reason = null)
{
    /// <summary>Whether the host has it at all, wherever it is.</summary>
    public bool Present => Found is ProgramFound.OnPath or ProgramFound.OffPath;
}

/// <summary>
/// Finds, once per connection, where each program a host is asked to run actually is.
/// </summary>
/// <remarks>
/// A login shell's PATH is not the PATH a command sees. Measured: on macOS <c>/opt/homebrew/bin</c> is
/// absent from the PATH of a command run over ssh, and in a WSL distribution <c>~/.dotnet</c> is absent
/// from the PATH of a command run with <c>wsl.exe --exec</c>, which is exactly where the .NET install
/// script puts the SDK. Either way the leg fails saying the tool is not installed, when it is.
/// </remarks>
public interface IHostProgramResolver
{
    /// <summary>
    /// Measures where <paramref name="programs"/> are on the host and returns the connection carrying
    /// what it found. A program already measured on <paramref name="connection"/> is not measured again,
    /// so a caller that needs one more program pays for that one only.
    /// </summary>
    /// <param name="connection">The connection to measure on.</param>
    /// <param name="programs">The program names, as a command would name them.</param>
    /// <param name="directories">
    /// Where to look when the PATH does not name one, <c>~</c> being the host's home: the built-in
    /// list for the harness's own SDK, and the repository's <c>toolSearchDirectories</c> for its
    /// declared tools, each one the host's platform names - see <see cref="ToolSearchDirectories.On"/>.
    /// A directory given that cannot be looked in leaves a program found nowhere else unknown,
    /// never missing.
    /// </param>
    /// <param name="budget">Longest one lookup may take.</param>
    /// <param name="cancellationToken">Stops the lookups.</param>
    Task<HostConnection> ResolveAsync(
        HostConnection connection,
        IReadOnlyList<string> programs,
        IReadOnlyList<string> directories,
        TimeSpan budget,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostProgramResolver"/>
public sealed class HostProgramResolver(LocalProgramResolver local, IHostCommandRunner hostCommands) : IHostProgramResolver
{
    /// <summary>ssh's own exit code for a failure of ssh itself, such as a connection or authentication failure.</summary>
    private const int SshFailed = 255;

    private readonly LocalProgramResolver _local = local;
    private readonly IHostCommandRunner _hostCommands = hostCommands;

    public async Task<HostConnection> ResolveAsync(
        HostConnection connection,
        IReadOnlyList<string> programs,
        IReadOnlyList<string> directories,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(directories);

        var found = new Dictionary<string, ProgramLocation>(connection.Programs, StringComparer.Ordinal);
        var home = new Lazy<Task<string?>>(() => HomeAsync(connection, budget, cancellationToken));

        foreach (var program in programs.Distinct(StringComparer.Ordinal).Where(program => !found.ContainsKey(program)))
        {
            // This machine is asked the way a leg on it will be: the same function the survey and the
            // run use, so install-missing-tools cannot call a tool present that a leg then cannot start.
            found[program] = connection.Host.Kind == HostKind.Local
                ? _local.Find(program, directories)
                : await ThereAsync(connection, program, directories, home, budget, cancellationToken).ConfigureAwait(false);
        }

        return connection with { Programs = found };
    }

    private async Task<ProgramLocation> ThereAsync(
        HostConnection connection,
        string program,
        IReadOnlyList<string> directories,
        Lazy<Task<string?>> home,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        // The name travels inside a command line an ssh server hands to a shell, so a name no shell
        // reads literally cannot be looked up at all, and saying so beats guessing at it.
        if (!RemoteCommandLine.IsLiteral(program, connection.Shell))
        {
            return new ProgramLocation(program, ProgramFound.Unreadable);
        }

        var onPath = await OnPathAsync(connection, program, budget, cancellationToken).ConfigureAwait(false);

        if (onPath.Found != ProgramFound.Nowhere)
        {
            return onPath;
        }

        // Every directory given is one this host was told to look in: its caller chose them for the
        // host's platform. Nothing to look in, and the PATH was everywhere there was to look.
        if (directories.Count == 0)
        {
            return onPath;
        }

        // cmd has no portable way to test one absolute path. Its host was told where to look, and
        // saying the program is not there would be a claim about directories nobody looked in.
        if (connection.Shell == RemoteShell.Cmd)
        {
            return Unlooked(program, "its shell, cmd, gives this no way to look in the directories searched for programs");
        }

        var (candidates, unsearched) = Candidates(program, directories, await home.Value.ConfigureAwait(false), connection.Shell);

        if (candidates.Count > 0)
        {
            // One listing for every candidate at once: a separate command for each would open a
            // separate ssh connection for each, and a host that is merely far away would take a
            // minute to answer.
            var listing = await RunAsync(connection, "ls", candidates, budget, cancellationToken).ConfigureAwait(false);

            if (!Answered(listing))
            {
                return new ProgramLocation(program, ProgramFound.Unreadable);
            }

            var lines = listing.StandardOutput
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            // A POSIX 'ls' given files prints each one that exists as it was given, and nothing else.
            // Anything else is some other shell's listing - PowerShell's is a table - and reading it
            // as "none of them exist" would call every program there missing.
            if (lines.Any(line => !candidates.Contains(line, StringComparer.Ordinal)))
            {
                return Unlooked(program, "what its shell printed for the directories searched for programs is not a listing this can read");
            }

            if (candidates.FirstOrDefault(lines.Contains) is { } path)
            {
                return new ProgramLocation(program, ProgramFound.OffPath, path);
            }
        }

        return unsearched is { } reason ? Unlooked(program, reason) : new ProgramLocation(program, ProgramFound.Nowhere);
    }

    /// <summary>A program that is not on the PATH, with directories it could be in that were not looked in.</summary>
    private static ProgramLocation Unlooked(string program, string why)
        => new(program, ProgramFound.Unreadable, Reason: $"'{program}' is not on the PATH there, and {why}");

    /// <summary>
    /// Whether the PATH of a command run without a login shell names <paramref name="program"/>.
    /// <c>command -v</c> is the POSIX way and a builtin of every shell an ssh server uses on a POSIX
    /// host; <c>where</c> is the Windows one, and is tried after it so that PowerShell, which has
    /// neither <c>command</c> nor a way to say which shell it is, is still answered for.
    /// </summary>
    private async Task<ProgramLocation> OnPathAsync(
        HostConnection connection,
        string program,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var answered = false;

        foreach (var lookup in Lookups(connection, program))
        {
            var result = await RunAsync(connection, lookup.Program, lookup.Arguments, budget, cancellationToken)
                .ConfigureAwait(false);

            answered |= Answered(result);

            // The answer is not used as the path: a Windows one holds spaces, which no command line
            // built here may carry, and a name on PATH needs no path anyway.
            if (result.Succeeded && FirstPath(result.StandardOutput) is { } path)
            {
                return new ProgramLocation(program, ProgramFound.OnPath, path);
            }
        }

        return new ProgramLocation(program, answered ? ProgramFound.Nowhere : ProgramFound.Unreadable);
    }

    /// <summary>The host's home directory, or <see langword="null"/> when it could not be measured.</summary>
    /// <remarks>
    /// Both transports start a command there: <c>wsl.exe</c> is given <c>--cd ~</c>, and an ssh server
    /// starts a command in the user's home directory.
    /// </remarks>
    private async Task<string?> HomeAsync(HostConnection connection, TimeSpan budget, CancellationToken cancellationToken)
    {
        var result = await RunAsync(connection, "pwd", [], budget, cancellationToken).ConfigureAwait(false);

        return result.Succeeded && result.TrimmedOutput.Length > 0 ? result.TrimmedOutput : null;
    }

    /// <summary>The lookups tried, in order, for one host.</summary>
    private static IEnumerable<(string Program, IReadOnlyList<string> Arguments)> Lookups(HostConnection connection, string program)
    {
        // wsl.exe --exec starts a program rather than a shell, so the shell that owns command -v has to
        // be started explicitly. Its argument never reaches a second shell, so a space in it is safe.
        if (connection.Host.Kind == HostKind.Wsl)
        {
            yield return ("sh", new[] { "-c", $"command -v {program}" });
            yield break;
        }

        if (connection.Shell != RemoteShell.Cmd)
        {
            yield return ("command", new[] { "-v", program });
        }

        yield return ("where", new[] { program });
    }

    /// <summary>
    /// The candidate paths for one program, in the order they are preferred, and why any directory
    /// that should have been looked in cannot be.
    /// </summary>
    private static (IReadOnlyList<string> Candidates, string? Unsearched) Candidates(
        string program,
        IReadOnlyList<string> directories,
        string? home,
        RemoteShell shell)
    {
        var candidates = new List<string>();
        var homeless = new List<string>();
        var foreign = new List<string>();
        var unspeakable = new List<string>();

        foreach (var directory in directories)
        {
            if (directory.StartsWith("~/", StringComparison.Ordinal) && home is null)
            {
                homeless.Add(directory);
                continue;
            }

            // Listed with a POSIX 'ls', which a Windows directory given to a host whose shell is not
            // cmd - PowerShell - cannot be.
            if (!ToolSearchDirectories.Names(directory, PlatformNames.Linux))
            {
                foreign.Add(directory);
                continue;
            }

            var path = (directory.StartsWith("~/", StringComparison.Ordinal) ? home!.TrimEnd('/') + directory[1..] : directory)
                + "/" + program;

            // A path holding a space cannot travel in a command line built here, so it is not sent in
            // a form the shell would split - and is not reported as looked in either.
            if (RemoteCommandLine.IsLiteral(path, shell))
            {
                candidates.Add(path);
            }
            else
            {
                unspeakable.Add(directory);
            }
        }

        var reasons = new List<string>();

        if (homeless.Count > 0)
        {
            reasons.Add($"{Quoted(homeless)} could not be looked in, because its home directory could not be read");
        }

        if (foreign.Count > 0)
        {
            reasons.Add($"{Quoted(foreign)} could not be looked in, because only a POSIX path can be listed there");
        }

        if (unspeakable.Count > 0)
        {
            reasons.Add($"{Quoted(unspeakable)} could not be looked in, because a path holding a space cannot be named in a command line sent there");
        }

        return (candidates, reasons.Count == 0 ? null : string.Join("; ", reasons));
    }

    private static string Quoted(IEnumerable<string> directories) => string.Join(", ", directories.Select(directory => $"'{directory}'"));

    private Task<ProcessResult> RunAsync(
        HostConnection connection,
        string program,
        IReadOnlyList<string> arguments,
        TimeSpan budget,
        CancellationToken cancellationToken)
        => _hostCommands.RunAsync(
            connection,
            new HostCommand { Program = program, Arguments = arguments, Timeout = budget },
            cancellationToken);

    /// <summary>
    /// Whether the host ran the lookup at all. A program that ran and found nothing exits non-zero; a
    /// connection that never opened is ssh's own 255, and one that hung has no exit code to read.
    /// </summary>
    private static bool Answered(ProcessResult result) => !result.TimedOut && result.ExitCode != SshFailed;

    private static string? FirstPath(string output)
        => output
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && (line.Contains('/', StringComparison.Ordinal)
                || line.Contains('\\', StringComparison.Ordinal)));
}
