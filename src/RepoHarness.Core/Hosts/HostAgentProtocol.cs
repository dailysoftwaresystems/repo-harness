using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// What one repo-harness asks the repo-harness on a host, and what it answers. A request travels as a
/// single JSON document on the host's standard input, so no argument ever passes through a shell.
/// </summary>
public static class HostAgentProtocol
{
    /// <summary>The hidden command a host serves requests with.</summary>
    public const string CommandName = "host-agent";

    /// <summary>
    /// The protocol version. Both ends are the same build by the time a request is sent, so a
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
}

/// <summary>What a request asks for.</summary>
public enum HostAgentRequestKind
{
    /// <summary>Which build answers, what the host is, and which emulators work there.</summary>
    Info,

    /// <summary>Run one repo-harness command in a directory on the host.</summary>
    Run,
}

/// <summary>A request to the repo-harness on a host.</summary>
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

    /// <summary>The command and its arguments, exactly as they would be typed after <c>repo-harness</c>. Run only.</summary>
    public List<string> Arguments { get; init; } = [];
}

/// <summary>A host's answer to an info request.</summary>
public sealed class HostAgentInfo
{
    /// <summary>The version of repo-harness that answered.</summary>
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
