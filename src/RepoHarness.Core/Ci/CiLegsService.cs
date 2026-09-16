using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Ci;

/// <summary>What check-ci-legs was asked to read.</summary>
/// <param name="Branch">The branch, or null for the one checked out.</param>
/// <param name="Limit">How many runs of each workflow, newest first.</param>
/// <param name="Runs">Named runs, read in the order given, instead of the newest ones.</param>
public sealed record CiLegsRequest(string? Branch, int Limit, IReadOnlyList<long> Runs)
{
    /// <summary>Runs read when nothing says otherwise: the newest one.</summary>
    public const int DefaultLimit = 1;
}

/// <summary>Reads the CI verdict of each leg, and separates a real failure from a budget overrun.</summary>
public interface ICiLegsService
{
    /// <summary>Reads the runs <paramref name="request"/> names and reports each leg.</summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="request">Which branch, and which runs.</param>
    /// <param name="cancellationToken">Stops the child processes.</param>
    /// <exception cref="HarnessException">
    /// CI could not be read: no workflow is declared, the branch cannot be named, no run was found, or
    /// a run answered no job rows at all. None of these is a pass.
    /// </exception>
    Task<CiLegsReport> CheckAsync(
        string startDirectory,
        CiLegsRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICiLegsService"/>
/// <remarks>
/// <para>
/// Verdicts come from job metadata, one job at a time, and never from the run rollup: a run whose
/// matrix was skipped is red and says nothing whatever about the tree, and reading it as evidence is
/// the mistake this command exists to stop repeating.
/// </para>
/// <para>
/// The budget has three sources, in this order: the job's own name, which the forge spells the matrix
/// values into and which is authoritative for the run that produced it; the workflow file in the
/// working tree, because the forge truncates a long job name and the leg nearest its cap is exactly
/// the one whose name is longest; and this repository's own <c>ci.legBudgetMinutes</c>. The third is
/// this repository's vocabulary rather than the guard's -- the guard read a matrix key and had no
/// declared budget of its own -- and it is last so a run's own value always wins. If none of the three
/// answers, the leg is NOT classified: a discriminator that invents its denominator is worse than one
/// that says it has none.
/// </para>
/// </remarks>
public sealed class CiLegsService(
    IHarnessContextLoader contextLoader,
    ICiJobSource jobSource,
    IGitClient gitClient,
    IFileSystem fileSystem) : ICiLegsService
{
    /// <summary>The directory a repository keeps its workflows in when configuration names none.</summary>
    public const string WorkflowsDirectory = ".github/workflows";

    /// <summary>
    /// The marker a leg's job name carries. A job whose name does not hold it is not a leg: it is the
    /// gate, the label check, or another job entirely, and its verdict is about something else.
    /// Matched anywhere in the name rather than at its start, because a called workflow prefixes its
    /// jobs with its own name: GitHub reports <c>ci / run-tests (win-msvc-release)</c>.
    /// </summary>
    private const string LegJobPrefix = "run-tests (";

    /// <summary>The two steps a leg's verdict is read from, matched by exact name.</summary>
    private const string BuildStep = "Build";

    /// <summary>The step whose failure the budget can explain.</summary>
    private const string TestStep = "Test";

    /// <summary>Fraction of its budget a green leg may reach before the budget is worth re-deriving.</summary>
    private const double WarningFraction = 0.8;

    /// <summary>
    /// The matrix values a forge spells into a job name end with the budget and one more number, so
    /// the budget is the second-to-last integer. Read in minutes, as the matrix declares it.
    /// </summary>
    private static readonly Regex BudgetInJobName =
        new(@",\s*(?<minutes>[0-9]+)\s*,\s*[0-9]+\s*\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>A workflow's own name, which is what the forge lists its runs under.</summary>
    private static readonly Regex WorkflowName =
        new(@"^name:\s*(?<name>.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly ICiJobSource _jobSource = jobSource;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<CiLegsReport> CheckAsync(
        string startDirectory,
        CiLegsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var root = context.Layout.RepositoryRoot;

        var workflows = ReadWorkflows(root, context.Config.Ci);
        var branch = await ResolveBranchAsync(root, request.Branch, cancellationToken).ConfigureAwait(false);

        var reports = new List<CiRunReport>();

        foreach (var workflow in workflows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runs = request.Runs.Count > 0
                ? request.Runs
                : await _jobSource
                    .ListRunsAsync(root, workflow.Identifier, branch, request.Limit, cancellationToken)
                    .ConfigureAwait(false);

            if (runs.Count == 0)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"No run of '{workflow.Identifier}' was found for branch {branch}, so CI was NOT read. "
                    + "This is not a pass.");
            }

            foreach (var runId in runs)
            {
                var run = await _jobSource.ReadRunAsync(root, runId, cancellationToken).ConfigureAwait(false);

                if (run.Jobs.Count == 0)
                {
                    // Fatal, never green. Zero job rows is indistinguishable from "every leg passed",
                    // and the two must never share an answer.
                    throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"Run {runId} answered no job rows at all, so nothing was verified. This is not a pass.");
                }

                reports.Add(Classify(run, workflow, context.Config.Ci));
            }

            if (request.Runs.Count > 0)
            {
                // Named runs are read once, not once per workflow: the caller already said which.
                break;
            }
        }

        return new CiLegsReport(branch, reports);
    }

    private static CiRunReport Classify(CiRun run, CiWorkflow workflow, CiSettings settings)
    {
        var legs = run.Jobs
            .Where(job => job.Name.Contains(LegJobPrefix, StringComparison.Ordinal))
            .Select(job => Classify(job, workflow, settings))
            .ToList();

        return new CiRunReport(
            run,
            workflow.Identifier,
            legs.Count > 0,
            [.. run.Jobs.Select(job => $"{job.Name}={job.Conclusion ?? "?"}")],
            legs);
    }

    private static CiLegOutcome Classify(CiJob job, CiWorkflow workflow, CiSettings settings)
    {
        var leg = LegName(job.Name);
        var build = job.Step(BuildStep);
        var test = job.Step(TestStep);

        // Only failure is failure. A cancelled, timed-out, neutral or skipped job is a different fact
        // with a different remedy, and calling it a failure sends a reader after code that never ran.
        var success = !string.Equals(job.Conclusion, CiConclusions.Failure, StringComparison.Ordinal);

        var (budget, source) = Budget(job.Name, leg, workflow, settings);
        var elapsed = test?.Elapsed?.TotalSeconds;

        var errors = new List<string>();
        var warnings = new List<string>();
        var overran = false;

        if (!success)
        {
            if (test is { } step && step.Failed)
            {
                if (elapsed is { } seconds && budget is { } cap)
                {
                    // At or over the cap the failure may be the budget rather than the code, so it is
                    // worded as an overrun. The leg stays red either way: the label says which
                    // question to ask about it, never that there is no question.
                    overran = seconds >= cap;

                    errors.Add(overran
                        ? $"the {TestStep} step failed at {Seconds(seconds)} of a {Seconds(cap)} budget ({source}): "
                        + "read this as a possible budget overrun, not a test failure. Re-derive the budget before raising it."
                        : $"the {TestStep} step failed after {Seconds(seconds)} of a {Seconds(cap)} budget ({source}), "
                        + "nowhere near the cap: a real test failure. Fix it; do not raise the cap.");
                }
                else
                {
                    errors.Add(
                        $"the {TestStep} step failed, and this leg has no budget to measure it against, so it is not "
                        + "classified: a discriminator that invents its denominator is worse than one that says it has none.");
                }
            }

            if (build is { } buildStep && buildStep.Failed)
            {
                // A build failure is never an overrun: a budget is spent running tests, so it cannot
                // explain a failure that happened before any ran.
                errors.Add($"the {BuildStep} step failed, which no budget explains.");
            }

            if (errors.Count == 0)
            {
                errors.Add($"the job concluded '{job.Conclusion ?? "nothing"}' without either named step failing.");
            }
        }
        else if (test is { Conclusion: CiConclusions.Success }
                 && elapsed is { } green
                 && budget is { } cap
                 && green > cap * WarningFraction)
        {
            warnings.Add(
                $"green, but the {TestStep} step took {Seconds(green)} of a {Seconds(cap)} budget ({source}); "
                + "re-derive the budget before it reds.");
        }

        return new CiLegOutcome(leg, job.Name, success, errors, overran, warnings, elapsed, budget, source);
    }

    /// <summary>The leg's name: the first matrix value the job's name carries.</summary>
    private static string LegName(string jobName)
    {
        var start = jobName.IndexOf(LegJobPrefix, StringComparison.Ordinal) + LegJobPrefix.Length;
        var rest = jobName[start..];
        var end = rest.IndexOfAny([',', ')']);

        return (end < 0 ? rest : rest[..end]).Trim();
    }

    private static (double? Seconds, string? Source) Budget(
        string jobName,
        string leg,
        CiWorkflow workflow,
        CiSettings settings)
    {
        if (BudgetInJobName.Match(jobName) is { Success: true } match
            && int.TryParse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture, out var fromName))
        {
            return (fromName * 60.0, "job name");
        }

        if (workflow.BudgetMinutes(leg) is { } fromFile)
        {
            return (fromFile * 60.0, "workflow");
        }

        return settings.LegBudgetMinutes > 0
            ? (settings.LegBudgetMinutes * 60.0, "ci.legBudgetMinutes")
            : (null, null);
    }

    private static string Seconds(double value) => $"{value.ToString("0", CultureInfo.InvariantCulture)}s";

    /// <summary>
    /// The workflows to read: the ones configuration names, or every file the conventional directory
    /// holds. An empty directory with nothing configured is a refusal, not an empty pass.
    /// </summary>
    private IReadOnlyList<CiWorkflow> ReadWorkflows(string root, CiSettings settings)
    {
        var paths = settings.Workflows.Count > 0
            ? settings.Workflows.Select(path => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))).ToList()
            : Enumerate(Path.Combine(root, WorkflowsDirectory.Replace('/', Path.DirectorySeparatorChar)));

        var workflows = new List<CiWorkflow>();

        foreach (var path in paths)
        {
            if (!_fileSystem.FileExists(path))
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"ci.workflows names '{path}', which is not a file, so CI was NOT read. This is not a pass.");
            }

            var text = _fileSystem.ReadAllText(path);
            var declared = WorkflowName.Match(text);

            workflows.Add(new CiWorkflow(
                declared.Success ? declared.Groups["name"].Value.Trim('\'', '"') : Path.GetFileName(path),
                text));
        }

        if (workflows.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"No workflow was found: ci.workflows names none and '{WorkflowsDirectory}' holds none, so CI was "
                + "NOT read. This is not a pass.");
        }

        return workflows;
    }

    private IReadOnlyList<string> Enumerate(string directory)
        => _fileSystem.DirectoryExists(directory)
            ? [.. _fileSystem.EnumerateFiles(directory, recursive: false)
                .Where(path => path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)]
            : [];

    private async Task<string> ResolveBranchAsync(string root, string? given, CancellationToken cancellationToken)
    {
        if (given is { Length: > 0 })
        {
            return given;
        }

        var result = await _gitClient
            .RunAsync(root, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var branch = result.Succeeded ? result.StandardOutput.Trim() : string.Empty;

        if (branch.Length == 0 || string.Equals(branch, "HEAD", StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                "There is no branch to ask about (a detached HEAD?), so CI was NOT read. Name one with --branch, "
                + "or name the runs with --run. This is not a pass.");
        }

        return branch;
    }

    /// <summary>One workflow file: what the forge lists its runs under, and its own text.</summary>
    /// <param name="Identifier">The workflow's declared name, or its file name when it declares none.</param>
    /// <param name="Text">The file, read once and searched for a leg's budget when a job name lost it.</param>
    private sealed record CiWorkflow(string Identifier, string Text)
    {
        /// <summary>
        /// The matrix budget the file declares for <paramref name="leg"/>, in minutes, or null.
        /// </summary>
        /// <remarks>
        /// Read only because the forge truncates a long job name out of its own budget field, and the
        /// leg with the longest name is the one running nearest its cap -- an instrument whose warning
        /// cannot fire on that leg is answering the adjacent question.
        /// </remarks>
        public int? BudgetMinutes(string leg)
        {
            var pattern = new Regex(
                $@"""name""\s*:\s*""{Regex.Escape(leg)}""[^\r\n]*?""ctest_budget_min""\s*:\s*(?<minutes>[0-9]+)",
                RegexOptions.CultureInvariant);

            return pattern.Match(Text) is { Success: true } match
                && int.TryParse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture, out var minutes)
                    ? minutes
                    : null;
        }
    }
}

/// <summary>Turns a CI legs report into what check-ci-legs prints.</summary>
public static class CiLegsReports
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>What check-ci-legs reports.</summary>
    /// <param name="report">What was measured.</param>
    /// <param name="json">Whether to write the per-leg document as JSON instead of as lines.</param>
    public static CommandOutcome Render(CiLegsReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<string> data = json ? [Json(report)] : Text(report);

        if (report.MatrixNeverRan)
        {
            // Its own answer, never a pass: the matrix is absent or skipped, so the run says nothing
            // about the tree, and reporting that as green is what this command exists to stop.
            return CommandOutcome.Failed(
                CiExit.MatrixDidNotRun,
                $"no job in {report.Runs.Count} run(s) of branch {report.Branch} is a leg, so nothing was verified "
                + "about the tree. This is not a pass.") with
            { Data = data };
        }

        if (report.Red.Count == 0)
        {
            return CommandOutcome.Ok($"every leg is green: {report.Legs.Count} leg(s) over {report.Runs.Count} run(s)") with
            { Data = data, Quiet = json };
        }

        return CommandOutcome.Failed(
            CiExit.LegRed,
            $"{report.Red.Count} leg(s) red: {report.RealFailures.Count} real failure(s) and "
            + $"{report.Overran.Count} possible budget overrun(s). The two have opposite remedies: fix the first, "
            + "re-derive the second. Reproduce a red leg locally; the logs behind these verdicts expire.") with
        { Data = data };
    }

    private static List<string> Text(CiLegsReport report)
    {
        var lines = new List<string>();

        foreach (var run in report.Runs)
        {
            lines.Add(
                $"run {run.Run.Id}  {run.Workflow}  {run.Run.Branch ?? report.Branch}  "
                + $"{Short(run.Run.HeadSha)}  {run.Run.CreatedAt}");

            if (!run.MatrixRan)
            {
                lines.Add("  the matrix did not run, so this run says nothing about the tree");
                lines.AddRange(run.JobsPresent.Select(job => $"    {job}"));
                continue;
            }

            foreach (var leg in run.Legs)
            {
                lines.Add($"  {leg.Leg,-26} {(leg.Success ? "green" : "RED"),-6} {Budget(leg)}");
                lines.AddRange(leg.Errors.Select(error => $"      {error}"));
                lines.AddRange(leg.Warnings.Select(warning => $"      {warning}"));
            }
        }

        return lines;
    }

    private static string Budget(CiLegOutcome leg)
    {
        var used = leg.TestSeconds is { } seconds ? $"{seconds.ToString("0", CultureInfo.InvariantCulture)}s" : "-";
        var cap = leg.BudgetSeconds is { } budget
            ? $"{budget.ToString("0", CultureInfo.InvariantCulture)}s cap ({leg.BudgetSource})"
            : "no budget";

        return $"{used} / {cap}";
    }

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "-";

    private static string Json(CiLegsReport report)
    {
        var node = new JsonObject
        {
            ["branch"] = report.Branch,
            ["runs"] = new JsonArray([.. report.Runs.Select(run => (JsonNode)new JsonObject
            {
                ["id"] = run.Run.Id,
                ["workflow"] = run.Workflow,
                ["headSha"] = run.Run.HeadSha,
                ["createdAt"] = run.Run.CreatedAt,
                ["matrixRan"] = run.MatrixRan,
                ["jobs"] = new JsonArray([.. run.JobsPresent.Select(job => (JsonNode?)JsonValue.Create(job))]),
                ["legs"] = new JsonArray([.. run.Legs.Select(leg => (JsonNode)new JsonObject
                {
                    ["leg"] = leg.Leg,
                    ["job"] = leg.Job,
                    ["success"] = leg.Success,
                    ["errors"] = new JsonArray([.. leg.Errors.Select(error => (JsonNode?)JsonValue.Create(error))]),
                    ["overran"] = leg.Overran,
                    ["warnings"] = new JsonArray([.. leg.Warnings.Select(warning => (JsonNode?)JsonValue.Create(warning))]),
                    ["testSeconds"] = leg.TestSeconds,
                    ["budgetSeconds"] = leg.BudgetSeconds,
                    ["budgetSource"] = leg.BudgetSource,
                })]),
            })]),
        };

        return node.ToJsonString(JsonOptions);
    }
}
