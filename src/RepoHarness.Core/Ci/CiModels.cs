namespace RepoHarness.Core.Ci;

/// <summary>One step of a CI job, as the job's metadata records it.</summary>
/// <param name="Name">The step's name, matched exactly.</param>
/// <param name="Conclusion">What the step concluded, or null when it never reached one.</param>
/// <param name="StartedAt">When it started, or null when the metadata does not say.</param>
/// <param name="CompletedAt">When it finished, or null when the metadata does not say.</param>
/// <remarks>
/// Steps are read from job metadata rather than from logs because the metadata outlives them. The
/// measured case: a run's logs and its failure artefacts had expired three days after the run, while
/// the per-step conclusions and timestamps that answer the same question were still there.
/// </remarks>
public sealed record CiStep(string Name, string? Conclusion, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    /// <summary>How long the step took, or null when either timestamp is missing.</summary>
    public TimeSpan? Elapsed
        => StartedAt is { } started && CompletedAt is { } completed && completed >= started
            ? completed - started
            : null;

    /// <summary>Whether the step reported failure. Only failure is failure; see <see cref="CiJob"/>.</summary>
    public bool Failed => string.Equals(Conclusion, CiConclusions.Failure, StringComparison.Ordinal);
}

/// <summary>One job of a CI run.</summary>
/// <param name="Name">The job's name, matrix values included.</param>
/// <param name="Conclusion">What the job concluded, or null when it never reached one.</param>
/// <param name="Steps">Its steps, in the order the run recorded them.</param>
public sealed record CiJob(string Name, string? Conclusion, IReadOnlyList<CiStep> Steps)
{
    /// <summary>The step with this exact name, or null. Names are matched exactly, never by prefix.</summary>
    /// <param name="name">The step's name.</param>
    public CiStep? Step(string name)
        => Steps.FirstOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal));
}

/// <summary>One CI run, with the jobs it recorded.</summary>
/// <param name="Id">The run's id, as the forge numbers it.</param>
/// <param name="HeadSha">The commit it ran on, or null when unknown.</param>
/// <param name="Branch">The branch it ran on, or null when unknown.</param>
/// <param name="CreatedAt">When it was created, as recorded, or null.</param>
/// <param name="Conclusion">The run rollup, which is never what a leg's verdict is read from.</param>
/// <param name="Jobs">Every job the run recorded.</param>
public sealed record CiRun(
    long Id,
    string? HeadSha,
    string? Branch,
    string? CreatedAt,
    string? Conclusion,
    IReadOnlyList<CiJob> Jobs);

/// <summary>The conclusions a forge records, spelled once so no comparison invents a spelling.</summary>
public static class CiConclusions
{
    /// <summary>The only conclusion that is red. Everything else is a different fact.</summary>
    /// <remarks>
    /// <c>cancelled</c>, <c>timed_out</c>, <c>neutral</c> and <c>skipped</c> are NOT failures, and a
    /// check that treats them as one sends a reader to debug code that never ran. Measured: a branch
    /// reported twenty-seven consecutive failed runs, of which every one before a certain date failed
    /// at a gate job with the whole matrix skipped -- "red for twenty commits" and "red on the runs
    /// that tested anything" are different facts, and only the second is about the tree.
    /// </remarks>
    public const string Failure = "failure";

    /// <summary>The conclusion of a job that did what was asked.</summary>
    public const string Success = "success";

    /// <summary>The conclusion of a job that never ran.</summary>
    public const string Skipped = "skipped";
}

/// <summary>What check-ci-legs found for one leg.</summary>
/// <param name="Leg">The leg's name, as <c>ci.legJobPattern</c>'s <c>leg</c> group captures it from the job's.</param>
/// <param name="Job">The job's full name, so a reader can find it in the forge.</param>
/// <param name="Success">Whether the job concluded anything other than failure.</param>
/// <param name="Errors">
/// What is wrong, worded for the remedy it calls for. An overrun is worded as an overrun and never as
/// a test failure, because the two have opposite remedies: one is a budget to re-derive, the other is
/// code to fix.
/// </param>
/// <param name="Overran">
/// Whether the test step's failure is at or over this leg's budget. Advisory: the leg is red either
/// way, and this only says which question to ask about it.
/// </param>
/// <param name="Unclassified">
/// Whether the test step failed with nothing to measure it against - no budget, or no duration - so the
/// failure is called neither a real one nor an overrun.
/// </param>
/// <param name="Warnings">Facts worth seeing that are not failures, such as a green leg near its cap.</param>
/// <param name="TestSeconds">How long the test step took, or null when the metadata does not say.</param>
/// <param name="BudgetSeconds">The leg's budget, or null when no source declared one.</param>
/// <param name="BudgetSource">Where the budget came from, so a reader can tell a run's own value from a file's.</param>
public sealed record CiLegOutcome(
    string Leg,
    string Job,
    bool Success,
    IReadOnlyList<string> Errors,
    bool Overran,
    bool Unclassified,
    IReadOnlyList<string> Warnings,
    double? TestSeconds,
    double? BudgetSeconds,
    string? BudgetSource);

/// <summary>What one run said about the legs.</summary>
/// <param name="Run">The run itself.</param>
/// <param name="Workflow">The workflow the run belongs to, as configuration named it.</param>
/// <param name="MatrixRan">Whether any job in the run is a leg at all.</param>
/// <param name="JobsPresent">Every job name and conclusion, reported when the matrix did not run.</param>
/// <param name="Legs">One outcome per leg, in the order the run recorded them.</param>
public sealed record CiRunReport(
    CiRun Run,
    string Workflow,
    bool MatrixRan,
    IReadOnlyList<string> JobsPresent,
    IReadOnlyList<CiLegOutcome> Legs);

/// <summary>What check-ci-legs measured across every run it read.</summary>
/// <param name="Branch">The branch asked about.</param>
/// <param name="Runs">One report per run, in the order they were read.</param>
public sealed record CiLegsReport(string Branch, IReadOnlyList<CiRunReport> Runs)
{
    /// <summary>Every leg across every run.</summary>
    public IReadOnlyList<CiLegOutcome> Legs => [.. Runs.SelectMany(run => run.Legs)];

    /// <summary>Legs that concluded failure.</summary>
    public IReadOnlyList<CiLegOutcome> Red => [.. Legs.Where(leg => !leg.Success)];

    /// <summary>Red legs whose test step failed at or over the budget.</summary>
    public IReadOnlyList<CiLegOutcome> Overran => [.. Red.Where(leg => leg.Overran)];

    /// <summary>Red legs that a budget does not explain: failed short of it, or failed in a way no budget explains.</summary>
    public IReadOnlyList<CiLegOutcome> RealFailures => [.. Red.Where(leg => !leg.Overran && !leg.Unclassified)];

    /// <summary>Red legs whose test step failed with nothing to measure it against, so called neither.</summary>
    public IReadOnlyList<CiLegOutcome> Unclassified => [.. Red.Where(leg => leg.Unclassified)];

    /// <summary>Whether no run read here carried the matrix at all, so none says anything about the tree.</summary>
    public bool MatrixNeverRan => Runs.Count > 0 && Runs.All(run => !run.MatrixRan);
}

/// <summary>Exit codes check-ci-legs answers with, from the per-command range.</summary>
/// <remarks>
/// A leg that ran and failed is a result the command was asked to find out. A matrix that never ran is
/// a different fact with a different remedy and gets its own code, because reporting it as a pass is
/// exactly the mistake this command exists to stop repeating. The instrument failing to look at all is
/// neither: that is reported with the shared codes, never as success.
/// </remarks>
public static class CiExit
{
    /// <summary>At least one leg concluded failure.</summary>
    public const int LegRed = 1;

    /// <summary>No job in any run read was a leg, so nothing was verified about the tree.</summary>
    public const int MatrixDidNotRun = 2;
}
