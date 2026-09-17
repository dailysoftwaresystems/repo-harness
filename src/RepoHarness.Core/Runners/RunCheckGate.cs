using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>What one run check decided, and why.</summary>
/// <param name="PredefinedRunner">The runner the check invoked.</param>
/// <param name="Passed">Whether it confirmed what the check expects.</param>
/// <param name="Reason">
/// What it found, phrased for the ledger. Recorded for a passing check too, so an excusal shows
/// the work that earned it rather than only its conclusion.
/// </param>
public sealed record RunCheckResult(string PredefinedRunner, bool Passed, string Reason);

/// <summary>What the gate decided about one expected exception.</summary>
/// <param name="Confirmed">Whether the entry may excuse the failure.</param>
/// <param name="Unconditional">
/// Whether the entry declared no checks at all. Reported so an unconditional excusal is visible in
/// the ledger rather than indistinguishable from a confirmed one.
/// </param>
/// <param name="Checks">Every check, in the order declared, passing and failing alike.</param>
public sealed record RunCheckGateResult(
    bool Confirmed,
    bool Unconditional,
    IReadOnlyList<RunCheckResult> Checks)
{
    /// <summary>Every check's reason, for the ledger.</summary>
    public IReadOnlyList<string> Reasons
        => [.. Checks.Select(check => $"{check.PredefinedRunner}: {check.Reason}")];
}

/// <summary>
/// Confirms an expected exception before it excuses anything.
/// </summary>
/// <remarks>
/// <para>
/// An entry with <c>runChecks</c> excuses nothing until every check passes. That is the whole point
/// of the gate: an unconditional excusal hides the regression it was written to explain, and it
/// hides it best on exactly the day the regression appears, because the entry was written when the
/// failure was benign and nothing since then has re-measured it.
/// </para>
/// <para>
/// The runner a check names is invoked through a delegate rather than reached directly, so the
/// gate's decisions are testable without a machine, a leg or a child process, and so the lead can
/// wire the real runner in one place. A check is one level deep: the runner it names is never the
/// one carrying the entry, which is refused here as well as by the configuration lint — a check
/// that invoked its own runner would either recurse or confirm itself.
/// </para>
/// </remarks>
public sealed class RunCheckGate(IHarnessOutput output)
{
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// Whether <paramref name="entry"/> may excuse the failure it matched.
    /// </summary>
    /// <param name="scope">The runner carrying the entry, and the legs it is available on.</param>
    /// <param name="entry">The entry that matched the failure.</param>
    /// <param name="window">
    /// The failing unit's own execution window, or <see langword="null"/> when none could be
    /// established. A check requiring steps fails without one.
    /// </param>
    /// <param name="steps">Every step recorded during the run.</param>
    /// <param name="invokeRunner">Runs a predefined runner by name and reports its outcome.</param>
    /// <param name="cancellationToken">Stops the confirmation.</param>
    /// <exception cref="HarnessException">A check names the runner carrying the entry.</exception>
    public async Task<RunCheckGateResult> ConfirmAsync(
        RunnerScope scope,
        ExpectedException entry,
        FailureWindow? window,
        IReadOnlyList<RunStep> steps,
        Func<string, CancellationToken, Task<RunOutcome>> invokeRunner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(invokeRunner);

        if (entry.RunChecks.Count == 0)
        {
            // Nothing to confirm. The lint is what keeps an entry from being unconditional; the
            // gate's job here is only to say plainly that nothing re-measured this one.
            return new RunCheckGateResult(Confirmed: true, Unconditional: true, []);
        }

        var declared = RunOutcome.From(entry);
        var results = new List<RunCheckResult>(entry.RunChecks.Count);

        foreach (var check in entry.RunChecks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (check.PredefinedRunner.Equals(scope.RunnerName, StringComparison.OrdinalIgnoreCase))
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"a run check of '{scope.RunnerName}' names '{scope.RunnerName}' itself. "
                    + "A check confirms an entry from outside it; naming its own runner would either "
                    + "recurse or confirm itself.");
            }

            results.Add(await RunCheckAsync(
                check,
                declared,
                window,
                steps,
                invokeRunner,
                cancellationToken).ConfigureAwait(false));
        }

        var confirmed = results.All(result => result.Passed);

        if (!confirmed)
        {
            _output.Detail(
                "run",
                $"the entry earned on {entry.EarnedOn} at {entry.EarnedAt} ({entry.Anchor}) "
                + "was not confirmed, so the failure stays genuine");
        }

        return new RunCheckGateResult(confirmed, Unconditional: false, results);
    }

    private async Task<RunCheckResult> RunCheckAsync(
        RunCheck check,
        RunOutcome declared,
        FailureWindow? window,
        IReadOnlyList<RunStep> steps,
        Func<string, CancellationToken, Task<RunOutcome>> invokeRunner,
        CancellationToken cancellationToken)
    {
        RunOutcome outcome;

        try
        {
            outcome = await invokeRunner(check.PredefinedRunner, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a failed check. The run reached no verdict, and a caller must
            // not read this as one.
            throw;
        }
        catch (HarnessException exception)
        {
            return new RunCheckResult(
                check.PredefinedRunner,
                Passed: false,
                $"could not be run ({exception.Message}), so nothing confirms the entry");
        }

        var failures = new List<string>();

        if (check.Expects.SameException == true && !outcome.SameExceptionAs(declared))
        {
            failures.Add($"expected the same exception as the entry "
                + $"(success {declared.Success}, warning {declared.Warning}, code {declared.ResultCode}, "
                + $"'{declared.Message}') but reported "
                + $"(success {outcome.Success}, warning {outcome.Warning}, code {outcome.ResultCode}, "
                + $"'{outcome.Message}')");
        }

        if (check.Expects.Success is { } expectedSuccess && outcome.Success != expectedSuccess)
        {
            failures.Add($"expected success {expectedSuccess} but reported {outcome.Success}");
        }

        if (check.Expects.ResultCode is { } expectedCode && outcome.ResultCode != expectedCode)
        {
            failures.Add($"expected result code {expectedCode} but reported {outcome.ResultCode}");
        }

        // Matched against what the run said and what its steps printed, because an author asking
        // for a marker a step emits means the run reported it, not that the harness's own summary
        // sentence happened to contain it. The documented example expects exactly that.
        if (check.Expects.Message is { } expectedMessage
            && !outcome.Texts.Any(text => text.Contains(expectedMessage, StringComparison.Ordinal)))
        {
            failures.Add($"expected a message containing '{expectedMessage}' but reported "
                + $"'{outcome.Message}'{(outcome.Output is { Length: > 0 } ? ", and no step printed it" : string.Empty)}");
        }

        failures.AddRange(StepFailures(check, window, steps));

        return failures.Count == 0
            ? new RunCheckResult(check.PredefinedRunner, Passed: true, Describe(check, window, steps))
            : new RunCheckResult(check.PredefinedRunner, Passed: false, string.Join("; ", failures));
    }

    private static IEnumerable<string> StepFailures(
        RunCheck check,
        FailureWindow? window,
        IReadOnlyList<RunStep> steps)
    {
        if (check.MinStepsInFailureWindow <= 0)
        {
            yield break;
        }

        if (window is null)
        {
            yield return "the failing unit's own execution window could not be established, "
                + "so no step can be attributed to it";
            yield break;
        }

        var counted = window.CountSteps(steps, check.MinStepSeconds);

        if (counted < check.MinStepsInFailureWindow)
        {
            yield return $"expected at least {check.MinStepsInFailureWindow} step(s) of at least "
                + $"{check.MinStepSeconds}s inside the window of '{window.Unit}' but counted {counted}";
        }
    }

    private static string Describe(RunCheck check, FailureWindow? window, IReadOnlyList<RunStep> steps)
    {
        if (check.MinStepsInFailureWindow <= 0 || window is null)
        {
            return "confirmed";
        }

        return $"confirmed, with {window.CountSteps(steps, check.MinStepSeconds)} step(s) of at least "
            + $"{check.MinStepSeconds}s inside the window of '{window.Unit}'";
    }
}
