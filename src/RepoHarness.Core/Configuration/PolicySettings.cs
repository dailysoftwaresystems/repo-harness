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
/// Continuous integration: the workflows carrying this repository's legs, and how long a leg may take.
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
    /// Minutes a CI leg may take before an overrun is reported. Zero means no budget, so nothing is
    /// ever reported as an overrun. An overrun is never reported as a failure: the remedy differs.
    /// </summary>
    public int LegBudgetMinutes { get; init; }
}
