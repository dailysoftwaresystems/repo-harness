using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>What performing an action file's predefined actions established.</summary>
/// <param name="Environment">
/// Variables later steps run with. A file's declared inputs reach its steps this way, so a step
/// reads a value the file names rather than one the machine happened to have.
/// </param>
/// <param name="Performed">Each predefined action performed, in the order the file declares it.</param>
public sealed record PredefinedActionResult(
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Performed);

/// <summary>Performing the predefined actions an action file declares.</summary>
public interface IPredefinedActionRunner
{
    /// <summary>Performs every predefined action in <paramref name="file"/>, before any step runs.</summary>
    /// <param name="file">The action file.</param>
    /// <param name="treeRoot">The tree the runner acts on.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="HarnessException">
    /// A required input has no value, or the tree is not at the commit a checkout names.
    /// </exception>
    Task<PredefinedActionResult> PerformAsync(
        ActionFile file,
        string treeRoot,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPredefinedActionRunner"/>
/// <remarks>
/// Performed before the first step rather than in declaration order, because both of them are about
/// the state a step then runs against: one settles what the steps read, the other settles which tree
/// they read it from. A run that discovered the wrong tree halfway through would already have
/// written into it.
/// </remarks>
public sealed class PredefinedActionRunner(IGitClient gitClient, IHarnessOutput output) : IPredefinedActionRunner
{
    /// <summary>What an input's variable is called, following the convention action files borrow.</summary>
    public const string InputPrefix = "INPUT_";

    private readonly IGitClient _gitClient = gitClient;
    private readonly IHarnessOutput _output = output;

    /// <inheritdoc/>
    public async Task<PredefinedActionResult> PerformAsync(
        ActionFile file,
        string treeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var performed = new List<string>();

        foreach (var step in file.Steps.Where(step => step.Uses != PredefinedAction.None))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Recorded inside each arm, and an unhandled action raises. Recorded after the switch
            // instead, an action this build does not implement would run nothing and then report
            // itself as performed — the run's own record asserting a checkout that never happened.
            switch (step.Uses)
            {
                case PredefinedAction.ReadInputs:
                    ReadInputs(file, environment);
                    performed.Add($"{step.Name} ({PredefinedActions.Spell(step.Uses)})");
                    break;

                case PredefinedAction.Checkout:
                    await CheckoutAsync(step, treeRoot, cancellationToken).ConfigureAwait(false);
                    performed.Add($"{step.Name} ({PredefinedActions.Spell(step.Uses)})");
                    break;

                default:
                    throw new HarnessException(
                        HarnessExit.InternalError,
                        $"Step '{step.Name}' uses '{step.Uses}', which this build knows the name of and "
                        + "cannot perform. Nothing was run.");
            }
        }

        return new PredefinedActionResult(environment, performed);
    }

    /// <summary>
    /// Puts the file's declared inputs where its steps can read them.
    /// </summary>
    /// <remarks>
    /// An input with no default and no value is refused rather than passed on as an empty string: a
    /// step reading an empty variable runs with whatever that means to it, and a corpus driver given
    /// an empty path walks the current directory.
    /// </remarks>
    private static void ReadInputs(ActionFile file, Dictionary<string, string> environment)
    {
        var missing = new List<string>();

        foreach (var input in file.Inputs)
        {
            if (input.Default is { } value)
            {
                environment[InputPrefix + input.Name.ToUpperInvariant()] = value;
                continue;
            }

            if (input.Required)
            {
                missing.Add(input.Name);
            }
        }

        if (missing.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"'{file.Path}' requires input(s) {string.Join(", ", missing)} and declares no default "
                + "for them, so its steps would run with nothing where a value belongs.");
        }
    }

    /// <summary>
    /// Confirms the tree is at the commit the step names.
    /// </summary>
    /// <remarks>
    /// It confirms rather than moves. The tree a leg runs against is the one sync created and the
    /// build compiled; moving it here would change the sources under a run that has already built
    /// them, which is precisely the failure the input fingerprinting elsewhere exists to catch —
    /// and it would do it deliberately. A tree that is not where the file says is a refusal naming
    /// both commits, and the remedy is to sync.
    /// </remarks>
    private async Task CheckoutAsync(ActionStep step, string treeRoot, CancellationToken cancellationToken)
    {
        if (step.Reference is not { Length: > 0 } reference)
        {
            // Nothing named, nothing to confirm: the tree is whatever sync left, which is what the
            // step asking for no particular commit is asking for.
            return;
        }

        var wanted = await _gitClient.ResolveCommitAsync(treeRoot, reference, cancellationToken).ConfigureAwait(false);

        if (wanted is null)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{reference}' names no commit in '{treeRoot}', so the step cannot confirm which tree "
                + "it is about to run against.");
        }

        var head = await _gitClient.ResolveCommitAsync(treeRoot, "HEAD", cancellationToken).ConfigureAwait(false);

        if (!string.Equals(head, wanted, StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{treeRoot}' is at {Short(head)} and the step names '{reference}' ({Short(wanted)}). "
                + "The tree is not moved here, because a build has already compiled these sources and "
                + "changing them under the run would report on a tree that never existed: sync the "
                + "leg to that commit first.");
        }

        _output.Detail(RunnerRunService.CommandName, $"{treeRoot} is at {Short(wanted)}, as '{reference}' names");
    }

    private static string Short(string? commit)
        => commit is null ? "an unknown commit" : commit[..Math.Min(12, commit.Length)];
}
