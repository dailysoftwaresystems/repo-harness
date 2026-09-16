using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Ci;

/// <summary>Reads CI run and job metadata for the repository a command is standing in.</summary>
public interface ICiJobSource
{
    /// <summary>The newest run ids of <paramref name="workflow"/> on <paramref name="branch"/>.</summary>
    /// <param name="repositoryRoot">The repository to ask about.</param>
    /// <param name="workflow">The workflow's name or file name.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="limit">How many runs, newest first.</param>
    /// <param name="cancellationToken">Stops the child process.</param>
    /// <exception cref="HarnessException">The forge could not be asked, so CI was not read.</exception>
    Task<IReadOnlyList<long>> ListRunsAsync(
        string repositoryRoot,
        string workflow,
        string branch,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>One run, with every job and step it recorded.</summary>
    /// <param name="repositoryRoot">The repository to ask about.</param>
    /// <param name="runId">The run.</param>
    /// <param name="cancellationToken">Stops the child processes.</param>
    /// <exception cref="HarnessException">The run could not be read, so CI was not read.</exception>
    Task<CiRun> ReadRunAsync(string repositoryRoot, long runId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICiJobSource"/>
/// <remarks>
/// <para>
/// Read-only by construction: this issues GET requests through the forge's own command line and
/// nothing else. It never labels, re-runs or pushes, because re-triggering CI is a person's call and
/// a leg reported red is a stop to read rather than something to retry until it passes.
/// </para>
/// <para>
/// The forge's tool finds which repository to answer for by running git, so git's own steering
/// variables are cleared before it starts, exactly as <see cref="Git.GitClient"/> clears them.
/// Measured: with another repository's <c>GIT_DIR</c> exported, the tool answered for THAT
/// repository even when the branch was given explicitly -- a fix that cleaned only the harness's own
/// git calls would have left this second channel open.
/// </para>
/// </remarks>
public sealed class GhCiJobSource(IProcessRunner processRunner) : ICiJobSource
{
    /// <summary>The forge's command line. The one dependency, and there is no fallback to a second.</summary>
    private const string GhExecutable = "gh";

    /// <summary>
    /// The variable that makes the forge's tool answer for a repository somebody else named. Refused
    /// rather than cleared: it is an explicit override, and this command reads only the CI of the tree
    /// it was pointed at, so silently ignoring it would answer a different question than the caller asked.
    /// </summary>
    private const string RepositoryOverrideVariable = "GH_REPO";

    private static readonly string[] InheritedGitEnvironment = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE"];

    private readonly IProcessRunner _processRunner = processRunner;

    public async Task<IReadOnlyList<long>> ListRunsAsync(
        string repositoryRoot,
        string workflow,
        string branch,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(
            repositoryRoot,
            [
                "run", "list",
                "--workflow", workflow,
                "--branch", branch,
                "--limit", limit.ToString(CultureInfo.InvariantCulture),
                "--json", "databaseId",
            ],
            cancellationToken).ConfigureAwait(false);

        using var document = Parse(output, $"the run list for '{workflow}' on {branch}");

        return [.. document.RootElement
            .EnumerateArray()
            .Select(run => run.TryGetProperty("databaseId", out var id) && id.TryGetInt64(out var value) ? value : 0)
            .Where(id => id != 0)];
    }

    public async Task<CiRun> ReadRunAsync(
        string repositoryRoot,
        long runId,
        CancellationToken cancellationToken = default)
    {
        var id = runId.ToString(CultureInfo.InvariantCulture);

        var runOutput = await RunAsync(
            repositoryRoot,
            ["api", $"repos/:owner/:repo/actions/runs/{id}"],
            cancellationToken).ConfigureAwait(false);

        using var run = Parse(runOutput, $"run {id}");

        var jobsOutput = await RunAsync(
            repositoryRoot,
            ["api", $"repos/:owner/:repo/actions/runs/{id}/jobs?per_page=100"],
            cancellationToken).ConfigureAwait(false);

        using var jobs = Parse(jobsOutput, $"the jobs of run {id}");

        return new CiRun(
            runId,
            Text(run.RootElement, "head_sha"),
            Text(run.RootElement, "head_branch"),
            Text(run.RootElement, "created_at"),
            Text(run.RootElement, "conclusion"),
            ReadJobs(jobs.RootElement));
    }

    private static IReadOnlyList<CiJob> ReadJobs(JsonElement root)
    {
        if (!root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. jobs.EnumerateArray().Select(job => new CiJob(
            Text(job, "name") ?? string.Empty,
            Text(job, "conclusion"),
            ReadSteps(job)))];
    }

    private static IReadOnlyList<CiStep> ReadSteps(JsonElement job)
    {
        if (!job.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. steps.EnumerateArray().Select(step => new CiStep(
            Text(step, "name") ?? string.Empty,
            Text(step, "conclusion"),
            Moment(step, "started_at"),
            Moment(step, "completed_at")))];
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Moment(JsonElement element, string name)
        => Text(element, name) is { Length: > 0 } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
                ? moment
                : null;

    private static JsonDocument Parse(string output, string subject)
    {
        try
        {
            return JsonDocument.Parse(output);
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{GhExecutable} answered something that is not JSON for {subject}, so CI was NOT read "
                + $"(this is not a pass): {ex.Message}");
        }
    }

    private async Task<string> RunAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(RepositoryOverrideVariable) is { Length: > 0 } named)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{RepositoryOverrideVariable} is set ({named}), so {GhExecutable} would answer for THAT repository; "
                + "this command reads only the CI of the tree it was pointed at. Unset it for this call. "
                + "CI was NOT read, and this is not a pass.");
        }

        if (_processRunner.FindExecutable(GhExecutable) is null)
        {
            throw new HarnessException(
                HarnessExit.ToolMissing,
                $"'{GhExecutable}' is not installed or not on PATH, so CI was NOT read. This is not a pass: an "
                + "instrument that could not look must never read as one.");
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var variable in InheritedGitEnvironment)
        {
            environment[variable] = null;
        }

        var result = await _processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = GhExecutable,
                Arguments = arguments,
                WorkingDirectory = repositoryRoot,
                Environment = environment,
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var said = result.StandardError.Trim();

            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{GhExecutable} {string.Join(' ', arguments)} failed, so CI was NOT read (this is not a pass): "
                + (said.Length > 0 ? said : $"it exited with code {result.ExitCode}."));
        }

        return result.StandardOutput;
    }
}
