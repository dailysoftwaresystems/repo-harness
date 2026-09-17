using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>
/// The operations one side of a sync asks the other to perform.
/// </summary>
/// <remarks>
/// A sync is the same conversation whichever host is on the far side, so the operations are named
/// once here and served by one command. The payload of a write travels inside the request the host
/// agent already carries on standard input, never on a command line: a command line is bounded, and
/// the words a remote shell reads literally are a small set that file content would leave at once.
/// </remarks>
public static class SyncServe
{
    /// <summary>The hidden command that serves these operations on a host.</summary>
    public const string CommandName = "sync-serve";

    /// <summary>Reports what the copy holds, as a manifest.</summary>
    public const string Manifest = "manifest";

    /// <summary>Reports whether the copy's root exists, and whether the harness created it.</summary>
    public const string Inspect = "inspect";

    /// <summary>Creates the copy's root, with its parents, and marks it as the harness's.</summary>
    public const string Create = "create";

    /// <summary>Makes the copy a git repository, which the harness there needs to find anything.</summary>
    public const string InitRepository = "init-repository";

    /// <summary>What mark a <see cref="Create"/> request asks for, spelled as the enum's own name.</summary>
    /// <param name="arguments">The request's arguments, the copy's root first.</param>
    /// <remarks>
    /// A request naming no mark, or one this build does not know, is a complete copy: that is what
    /// every request meant before a mark was carried, and reading it as anything else would turn a
    /// finished copy into one that still needs somebody's permission.
    /// </remarks>
    public static CopyMark MarkIn(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return arguments.Count > 1 && Enum.TryParse<CopyMark>(arguments[1], ignoreCase: false, out var mark)
            ? mark
            : CopyMark.Complete;
    }

    /// <summary>Writes one file into the copy.</summary>
    public const string Write = "write";

    /// <summary>Deletes one file from the copy.</summary>
    public const string Delete = "delete";

    /// <summary>Reads one file out of the copy.</summary>
    public const string Read = "read";

    /// <summary>How an answer is written, and read back, so both ends agree without guessing.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // A shape one end does not recognise is a hard failure rather than silent data loss, as it
        // is everywhere else this tool reads JSON.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// The line an answer is written on, marked so it is told apart from anything else the command
    /// prints. Without the mark a diagnostic written to the same stream would be parsed as the answer.
    /// </summary>
    public const string AnswerPrefix = "sync-serve-answer ";

    /// <summary>Writes an answer for the other end to read.</summary>
    /// <typeparam name="T">The answer's shape.</typeparam>
    /// <param name="answer">The answer.</param>
    public static string Answer<T>(T answer) => AnswerPrefix + JsonSerializer.Serialize(answer, JsonOptions);

    /// <summary>Reads an answer the other end wrote, or null when the line is not one.</summary>
    /// <typeparam name="T">The answer's shape.</typeparam>
    /// <param name="line">A line the far side printed.</param>
    /// <exception cref="HarnessException">The line is an answer this build cannot read.</exception>
    public static T? ReadAnswer<T>(string line)
        where T : class
    {
        if (line is null || !line.StartsWith(AnswerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(line[AnswerPrefix.Length..], JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The far side answered in a shape this build cannot read: {ex.Message}");
        }
    }
}

/// <summary>What the far side holds, as a manifest.</summary>
/// <param name="Entries">Every transferable file in the copy.</param>
public sealed record SyncManifestAnswer(IReadOnlyList<SyncEntry> Entries);

/// <summary>What the far side's root looks like.</summary>
/// <param name="Exists">Whether the root directory is there.</param>
/// <param name="Mark">What the harness has recorded about it.</param>
public sealed record SyncInspectAnswer(bool Exists, CopyMark Mark);

/// <summary>What a copy's marker says about how it came to be.</summary>
public enum CopyMark
{
    /// <summary>There is no marker: whatever is there, this tool did not make it.</summary>
    None,

    /// <summary>A copy this tool made, and finished making.</summary>
    Complete,

    /// <summary>
    /// A copy this tool began taking over and did not finish. Neither the checkout somebody had nor
    /// a copy of the source: some of what was there is already gone, and what is left is not what a
    /// plan would now report, because a plan can only see what survived.
    /// </summary>
    AdoptionStopped,
}

/// <summary>One file's bytes, base64 encoded so they survive a line of text intact.</summary>
/// <param name="Content">The file's bytes.</param>
/// <param name="ContentHash">
/// The SHA-256 the far side computed of those bytes, before they were encoded and sent. Carried so
/// the machine that asked can check what arrived against what was read, rather than against itself:
/// a hash taken here of the bytes that arrived agrees with them whatever happened on the way.
/// </param>
public sealed record SyncFileAnswer(string Content, string ContentHash);
