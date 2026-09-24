using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// What one DssHarness asks the DssHarness on a host, and what it answers. A request travels as one
/// line of JSON on the host's standard input, so no argument ever passes through a shell. The machine that
/// asked then holds that input open until the host has finished: its end means the machine that asked has
/// gone, and the host cancels whatever the request started.
/// </summary>
public static class HostAgentProtocol
{
    /// <summary>The hidden command a host serves requests with.</summary>
    public const string CommandName = "host-agent";

    /// <summary>The option that has a host report a defect of its own with its stack trace.</summary>
    public const string VerboseOption = "--verbose";

    /// <summary>
    /// The protocol version. Both ends are the same build by the time a run request is sent, so a
    /// difference is a defect rather than something to negotiate.
    /// </summary>
    /// <remarks>
    /// Raised whenever a request or an answer changes shape. A host reads the version before anything
    /// else, so one on another build refuses a request as coming from another protocol, naming both
    /// and its own version. With the number left as it was, the same host refuses the request over
    /// whichever field it happens not to know, which says nothing about why.
    /// </remarks>
    public const int Version = 4;

    /// <summary>
    /// How requests and answers are written. Dictionaries and lists are read with the converters
    /// configuration is read with, so an emulator arrives on a host as config.json declared it: its
    /// names compared ignoring case, and no list holding a null.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new CaseInsensitiveDictionaryConverter(),
            new NonNullListConverter(),
        },
    };

    /// <summary>
    /// Commands a host is never asked to run. A host that passed the work on to another host would leave
    /// the machine that asked unable to say where anything ran.
    /// </summary>
    public static IReadOnlyList<string> NotForwardable { get; } = [CommandName, HostExecService.CommandName];

    /// <summary>Whether <paramref name="command"/> is one a host is never asked to run, in whatever case it is typed.</summary>
    public static bool IsNotForwardable(string command) => NotForwardable.Contains(command, StringComparer.OrdinalIgnoreCase);

    /// <summary>A new value for <see cref="HostAgentRequest.Nonce"/>: random, so no output of a command holds it by chance.</summary>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The line a host writes to standard error last, once the command a run request named has finished,
    /// carrying that command's exit code. The machine that asked takes the exit code from this line rather
    /// than from the transport: ssh and wsl.exe exit with codes of their own when a connection fails, and a
    /// line that never arrives is how such a failure is told apart from the command's own result.
    /// </summary>
    public static string CompletionLine(string nonce, int exitCode)
        => $"{CommandName}: finished {nonce} {exitCode.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The line a host writes on each stream first, before it serves a run request, so that the machine that
    /// asked can tell what the agent says from what the host's login shell said before it.
    /// </summary>
    /// <remarks>
    /// A login shell writes to the same streams the command does, and whatever it writes arrives first. One
    /// consumer's Mac sources emsdk's environment script on every session, which prints the account's home
    /// layout - the user's name among it - and a machine relaying a leg's output published it into a ledger,
    /// a CI log and a chat transcript. What a host's profile says is not this run's output and does not
    /// belong in it; the agent's own words, from here on, are.
    /// </remarks>
    public static string StartedLine(string nonce)
        => $"{CommandName}: serving {nonce}";

    /// <summary>Whether <paramref name="line"/> carries the started line of the request that sent <paramref name="nonce"/>.</summary>
    /// <remarks>
    /// Matched as the end of the line rather than the whole of it. A login profile whose last write has no
    /// trailing newline - a prompt, an escape sequence, an <c>echo -n</c> - glues its bytes onto the first
    /// line the agent writes, which is this one. Held to the whole line, such a host would never open the
    /// gate at all, and every run on it would report as one that never said how it finished.
    /// </remarks>
    public static bool IsStartedLine(string line, string nonce)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        return line.TrimEnd().EndsWith(StartedLine(nonce), StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="text"/> from the agent's started line on, or the whole of it where that line is not
    /// in it: what a message quotes from a whole captured stream, rather than the host's login shell too.
    /// </summary>
    /// <param name="text">Everything a stream carried.</param>
    /// <param name="nonce">The request's nonce.</param>
    /// <remarks>
    /// The capture is kept whole for the reader who asks for it, and trimmed wherever it is quoted into
    /// something the harness says: a host that never reached its agent has nothing else to show, so there
    /// the whole of it is the answer. One consumer's profile prints the account's home layout on every
    /// session, and an excerpt of a short stream is otherwise nothing but that.
    /// </remarks>
    public static string SinceServing(string text, string nonce)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        var started = text.LastIndexOf(StartedLine(nonce), StringComparison.Ordinal);

        return started < 0 ? text : text[(started + StartedLine(nonce).Length)..].TrimStart('\r', '\n');
    }

    /// <summary>
    /// Whether <paramref name="line"/> is one the agent itself wrote under its own name, which is relayed
    /// even before the started line: a request refused before it could be read carries no nonce to mark.
    /// </summary>
    /// <param name="line">A line the host wrote.</param>
    public static bool IsAgentsOwnLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.TrimStart().StartsWith(CommandName + ": ", StringComparison.Ordinal);
    }

    /// <summary>Reads <paramref name="line"/> as the completion line of the request that carried <paramref name="nonce"/>.</summary>
    public static bool TryReadCompletionLine(string line, string nonce, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        exitCode = 0;
        var prefix = $"{CommandName}: finished {nonce} ";

        return line.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(line.AsSpan(prefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exitCode);
    }
}

/// <summary>What a request asks for.</summary>
public enum HostAgentRequestKind
{
    /// <summary>Which build answers, what the host is, and which emulators work there.</summary>
    Info,

    /// <summary>Run one DssHarness command in a directory on the host.</summary>
    Run,
}

/// <summary>A request to the DssHarness on a host.</summary>
public sealed class HostAgentRequest
{
    /// <summary>The protocol the request is written in.</summary>
    public int Protocol { get; init; } = HostAgentProtocol.Version;

    /// <summary>What is asked.</summary>
    public required HostAgentRequestKind Kind { get; init; }

    /// <summary>The emulators to check, by name. Info only.</summary>
    public Dictionary<string, EmulatorConfig> Emulators { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The developer environments to look for, by name. Info only.</summary>
    public Dictionary<string, DeveloperEnvironmentConfig> DeveloperEnvironments { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The programs to find there, the way a leg on that host will start them. Info only.
    /// </summary>
    public List<string> Programs { get; init; } = [];

    /// <summary>
    /// The repository's <c>toolSearchDirectories</c>, of which the host takes its own platform's.
    /// Info only.
    /// </summary>
    /// <remarks>
    /// Sent whole rather than chosen here, because only the host knows which platform it is until it
    /// has answered, and asking twice would be a second round trip for one question.
    /// </remarks>
    public Dictionary<string, List<string>> ToolSearchDirectories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The command that keeps the host awake while this request is served, as the configuration declares it
    /// for that host, or empty where it declares none. Run only for a <see cref="HostAgentRequestKind.Run"/>.
    /// </summary>
    /// <remarks>
    /// Carried rather than read there, because the host's copy has no configuration until a first sync has
    /// put one in it - and a first sync is the longest one, the one that most needs the host to stay awake.
    /// Measured by a consumer: a first sync of a worktree's copy to a Mac ran past the host's wake and the
    /// legs were left unavailable. The command's <c>{pid}</c> becomes the agent's own process there, so it
    /// ends with the request whatever happens to this end of the connection.
    /// </remarks>
    public List<string> KeepAwake { get; init; } = [];

    /// <summary>
    /// What the host declares under <c>env</c> for itself, which the <see cref="KeepAwake"/> command starts
    /// under, as a leg's own work does. Empty where the host declares none.
    /// </summary>
    /// <remarks>
    /// Carried with the command, for the same reason the command is: the host's copy has no configuration to
    /// read until a first sync has put one there. Without it a command that starts for a leg - because a leg
    /// supplies the host's environment - would not start here.
    /// </remarks>
    public Dictionary<string, string> KeepAwakeEnvironment { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Where the survey found this host's programs, which the <see cref="KeepAwake"/> command is looked for
    /// in, as a leg's own programs are. Empty where nothing was surveyed.
    /// </summary>
    public List<string> KeepAwakeDirectories { get; init; } = [];

    /// <summary>
    /// The directory the command starts in on the host, absolute or from the home directory: the host's copy of the
    /// tree it runs in, or, for a sync's own operations, the directory that copy is kept in, which is there before the
    /// copy is. Run only.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>The command and its arguments, exactly as they would be typed after <c>DssHarness</c>. Run only.</summary>
    public List<string> Arguments { get; init; } = [];

    /// <summary>
    /// A value the machine that asked chose for this request, repeated in the host's completion line so
    /// that nothing the command prints can be taken for that line. Run only.
    /// </summary>
    public string? Nonce { get; init; }
}

/// <summary>A host's answer to an info request.</summary>
public sealed class HostAgentInfo
{
    /// <summary>The version of DssHarness that answered.</summary>
    public required string Version { get; init; }

    /// <summary>The SHA-256 of the assembly that answered.</summary>
    public required string AssemblySha256 { get; init; }

    /// <summary>The host's operating system, in configuration's words.</summary>
    public required string Os { get; init; }

    /// <summary>The host's processor, in configuration's words.</summary>
    public required string Processor { get; init; }

    /// <summary>What checking each requested emulator found.</summary>
    public Dictionary<string, EmulatorCheck> Emulators { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What looking for each requested developer environment found.</summary>
    public Dictionary<string, DeveloperEnvironmentCheck> DeveloperEnvironments { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each requested program is there, each carrying the name it was asked for.</summary>
    /// <remarks>
    /// A list rather than a map keyed by name. A name compares exactly - cmake and CMake are two files
    /// on Linux, and a repository can ask about both - while a map in this protocol is read back
    /// ignoring case, as configuration's names are, and refuses an answer holding both.
    /// </remarks>
    public List<ProgramLocation> Programs { get; init; } = [];

    /// <summary>
    /// The directories a program asked for by name was found in there, on the PATH or off it, in the
    /// order the search looked: what a leg there appends to the PATH of every process it starts.
    /// </summary>
    public List<string> ProgramDirectories { get; init; } = [];
}

/// <summary>Whether an emulator works on a host.</summary>
/// <param name="Available">Whether legs can run through it there.</param>
/// <param name="Reason">Why they cannot, when they cannot.</param>
/// <param name="Witnessed">What its witness printed, when it worked.</param>
public sealed record EmulatorCheck(bool Available, string? Reason, string? Witnessed)
{
    /// <summary>An emulator legs cannot run through, and why.</summary>
    public static EmulatorCheck Unavailable(string reason) => new(false, reason, null);
}
