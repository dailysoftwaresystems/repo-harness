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
    public const int Version = 1;

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

    /// <summary>The host's copy of the repository, absolute or from the home directory. Run only.</summary>
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
