namespace RepoHarness.Core.Configuration;

/// <summary>
/// The repository's line-ending policy, and what stands outside it.
/// </summary>
/// <remarks>
/// The policy itself is never restated here. It is declared in <c>.gitattributes</c> and read back
/// through <c>git check-attr</c>, so one statement of it governs both what git stores and what the
/// harness rewrites. A second copy would eventually disagree with the first, and the file that
/// decides a checkout's bytes would not be the one anybody edited.
/// </remarks>
public sealed class LineEndingSettings
{
    /// <summary>
    /// Paths the policy does not reach, relative to the repository root. A generated tree whose
    /// producer decides its own bytes belongs here; nothing else should.
    /// </summary>
    public List<string> Exclude { get; init; } = [];
}

/// <summary>
/// Continuous integration: the workflows carrying this repository's legs, how their jobs and steps are
/// named, and how long a leg may take.
/// </summary>
/// <remarks>
/// A CI leg that fails and a CI leg that ran out of time are different facts with different remedies,
/// and a check that reports them as one number tells a reader to debug code that never ran.
/// </remarks>
public sealed class CiSettings
{
    /// <summary>
    /// Workflow files, relative to the repository root, whose jobs carry this repository's legs.
    /// Empty means every workflow under <c>.github/workflows</c>.
    /// </summary>
    public List<string> Workflows { get; init; } = [];

    /// <summary>
    /// Minutes a CI leg may take before an overrun is reported, where neither its job's name nor its
    /// workflow gives it a budget. Zero means none. An overrun is never reported as a failure: the
    /// remedy differs.
    /// </summary>
    public int LegBudgetMinutes { get; init; }

    /// <summary>
    /// A .NET regular expression matched against each job's name, as the forge reports it: a job it
    /// matches is a leg, named by its <c>leg</c> group, and a <c>budget</c> group, where it has one,
    /// gives the leg's budget in minutes. Required by check-ci-legs, which assumes no job naming of its
    /// own: GitHub names a matrix job <c>job (values)</c>, in the order its matrix declares them, and a
    /// workflow decides what the values are.
    /// </summary>
    public string? LegJobPattern { get; init; }

    /// <summary>The step a leg builds in, by its exact name. Required by check-ci-legs.</summary>
    public string? BuildStep { get; init; }

    /// <summary>The step a leg tests in, whose failure a budget can explain, by its exact name. Required by check-ci-legs.</summary>
    public string? TestStep { get; init; }

    /// <summary>
    /// A .NET regular expression matched against each workflow file's text, for the budget of a leg whose
    /// job name gave none - a long name the forge cut short, say: <see cref="LegPlaceholder"/> in it stands
    /// for the leg's name, and its <c>budget</c> group gives the minutes. Optional.
    /// </summary>
    public string? WorkflowBudgetPattern { get; init; }

    /// <summary>What stands for a leg's name in <see cref="WorkflowBudgetPattern"/>.</summary>
    public const string LegPlaceholder = "{leg}";
}
