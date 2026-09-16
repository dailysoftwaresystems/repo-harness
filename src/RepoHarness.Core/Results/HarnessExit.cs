using System.Reflection;

namespace RepoHarness.Core.Results;

/// <summary>
/// Exit codes shared by every command. Codes 1-9 are reserved for per command
/// meanings (see <see cref="Git.VerifyGitStatus"/>), so a command specific contract
/// never collides with a cross cutting one.
/// </summary>
/// <remarks>
/// Two rules hold everywhere: zero always means success, and "the thing you asked
/// about failed" never shares a code with "the harness itself could not run",
/// because the two call for different responses.
/// </remarks>
public static class HarnessExit
{
    /// <summary>The command did what was asked.</summary>
    public const int Success = 0;

    /// <summary>Arguments were missing, unknown, or mutually exclusive.</summary>
    public const int UsageError = 10;

    /// <summary>No harness configuration was found. Run <c>DssHarness init</c>.</summary>
    public const int NotInitialized = 11;

    /// <summary>config.json is missing, unparseable, or failed validation.</summary>
    public const int ConfigInvalid = 12;

    /// <summary>A precondition refused the request: dirty tree, name taken, lock held.</summary>
    public const int Refused = 13;

    /// <summary>A required external tool is not installed, or could not be started.</summary>
    public const int ToolMissing = 14;

    /// <summary>
    /// A host the command was pointed at could not be used: it could not be reached, it has no
    /// .NET SDK, DssHarness could not be brought to this machine's version there, or its copy of
    /// the repository does not exist. Distinct from <see cref="CommandFailed"/>: nothing ran there.
    /// </summary>
    public const int HostUnavailable = 15;

    /// <summary>The wrapped command ran and reported failure.</summary>
    public const int CommandFailed = 20;

    /// <summary>
    /// The work ran and nothing failed, but not every unit of it reached a verdict.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Success"/> because a leg that never ran is not a leg that passed,
    /// and distinct from <see cref="CommandFailed"/> because nothing reported failure. A gate
    /// comparing runs needs to tell "the tests passed" from "the tests never ran", and a single
    /// zero for both is how a switched-off machine reads as a green build.
    /// </remarks>
    public const int Incomplete = 21;

    /// <summary>
    /// The harness itself failed unexpectedly. Distinct from every other code
    /// because it means the tool has a defect, not that the request was wrong.
    /// </summary>
    public const int InternalError = 70;

    /// <summary>
    /// The run was interrupted before it finished, so it reached no verdict, and a caller
    /// must not read this as a red one. A command stops having done nothing, except a
    /// deletion already past the point where it can be undone, which says what it left
    /// behind. The value follows the shell convention for an interrupted process.
    /// </summary>
    public const int Cancelled = 130;

    /// <summary>
    /// Every shared code with its explanation, read from the constants themselves so
    /// that <c>DssHarness help exit-codes</c> cannot drift away from the values commands
    /// actually return. The table in docs/architecture.md is maintained by hand and is
    /// not covered by this.
    /// </summary>
    public static IReadOnlyList<ExitCodeDescription> All { get; } = BuildDescriptions();

    /// <summary>Describes one shared exit code, or <see langword="null"/> if it is command specific.</summary>
    public static ExitCodeDescription? Describe(int exitCode)
        => All.FirstOrDefault(description => description.Code == exitCode);

    private static IReadOnlyList<ExitCodeDescription> BuildDescriptions()
    {
        var explanations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Success)] = "The command did what was asked.",
            [nameof(UsageError)] = "Arguments were missing, unknown, or mutually exclusive.",
            [nameof(NotInitialized)] = "No harness configuration found; run 'DssHarness init'.",
            [nameof(ConfigInvalid)] = "config.json is missing, unparseable, or failed validation.",
            [nameof(Refused)] = "A precondition refused the request (dirty tree, name taken, lock held).",
            [nameof(ToolMissing)] = "A required external tool is not installed, or could not be started.",
            [nameof(HostUnavailable)] = "A host could not be reached, or DssHarness could not run there; nothing ran on it.",
            [nameof(CommandFailed)] = "The wrapped command ran and reported failure.",
            [nameof(Incomplete)] = "Ran with nothing failing, but a leg reached no verdict; it is not a pass.",
            [nameof(InternalError)] = "The harness itself failed unexpectedly; this is a defect in the tool.",
            [nameof(Cancelled)] = "The run was interrupted before it finished; a deletion already under way says what it left.",
        };

        return [.. typeof(HarnessExit)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .Select(field => new ExitCodeDescription(
                (int)field.GetRawConstantValue()!,
                field.Name,
                explanations.TryGetValue(field.Name, out var text) ? text : string.Empty))
            .OrderBy(description => description.Code)];
    }
}

/// <summary>One documented exit code.</summary>
/// <param name="Code">The numeric value a command returns.</param>
/// <param name="Name">The constant's name.</param>
/// <param name="Explanation">What it means for the caller.</param>
public sealed record ExitCodeDescription(int Code, string Name, string Explanation);
