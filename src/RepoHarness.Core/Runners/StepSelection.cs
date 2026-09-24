using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>
/// Which of an action's steps one run runs: every step that is not manual, the steps a runner declares
/// under <c>steps</c>, or the manual steps <c>run --manual-step</c> names - each with what it needs.
/// </summary>
/// <remarks>
/// Decided once, from the action as it was read, and handed on as an action holding only the steps
/// chosen. Everything that reads an action - the tool policy, the names a step may use, the inputs a
/// run may be given, the programs a host is asked for, the leg that would run nothing - then reads
/// exactly what will run, as it already reads only the steps a leg's operating system runs.
/// </remarks>
public sealed record StepSelection
{
    /// <summary>The option that names manual steps to run.</summary>
    public const string Option = "--manual-step";

    /// <summary>A run that names no step: every step that is not manual.</summary>
    public static StepSelection Default { get; } = new();

    /// <summary>The steps the runner declares under <c>steps</c>; empty where it declares none.</summary>
    public IReadOnlyList<string> RunnerSteps { get; init; } = [];

    /// <summary>The manual steps <c>run --manual-step</c> named; empty where it named none.</summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = [];

    /// <summary>What a run of <paramref name="runner"/> runs, given the manual steps its command line named.</summary>
    /// <param name="runner">The runner.</param>
    /// <param name="manualSteps">What <see cref="Option"/> named, read by <see cref="Names"/>.</param>
    public static StepSelection For(RunnerConfig runner, IReadOnlyList<string> manualSteps)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(manualSteps);

        return new StepSelection { RunnerSteps = runner.Steps ?? [], ManualSteps = manualSteps };
    }

    /// <summary>
    /// The step names <paramref name="values"/> give - each value one name or several joined by commas,
    /// as <c>--legs</c> takes them - or none where the option was left out.
    /// </summary>
    /// <param name="values">What each <see cref="Option"/> was given, or <see langword="null"/> when it was not given.</param>
    /// <exception cref="HarnessException">The option was given and names no step.</exception>
    /// <remarks>
    /// Given and naming nothing is refused rather than read as left out: <c>--manual-step "$STEP"</c>
    /// with the variable never set is not a request for a run that names no step, and read as one it
    /// would run the action's other steps on every leg while the command line said otherwise.
    /// </remarks>
    public static IReadOnlyList<string> Names(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        var names = values
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names.Count > 0
            ? names
            : throw new HarnessException(
                HarnessExit.UsageError,
                $"{Option} was given no step name; leave it out to run the steps a run of the runner runs.");
    }

    /// <summary>The steps of <paramref name="file"/> this selection runs.</summary>
    /// <param name="runnerName">The runner, as a refusal names it.</param>
    /// <param name="file">The action, as it was read.</param>
    /// <exception cref="HarnessException">
    /// <see cref="Option"/> names a step the action does not declare, or one that is not manual; the runner's
    /// <c>steps</c> names a step the action does not declare; or the run would run no step at all.
    /// </exception>
    public SelectedSteps Apply(string runnerName, ActionFile file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerName);
        ArgumentNullException.ThrowIfNull(file);

        var manual = file.Steps.Where(step => step.Manual).Select(step => step.Name).ToList();
        var declared = file.Steps.Select(step => step.Name).ToList();

        IReadOnlyList<string> named;

        if (ManualSteps.Count > 0)
        {
            var available = manual.Count == 0
                ? "it declares no manual step"
                : $"its manual steps are {string.Join(", ", manual)}";

            var unknown = ManualSteps.Where(name => !declared.Contains(name, StringComparer.Ordinal)).ToList();

            if (unknown.Count > 0)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{Option} names {Quoted(unknown)}, which '{file.Path}' does not declare; {available}.");
            }

            var automatic = ManualSteps.Where(name => !manual.Contains(name, StringComparer.Ordinal)).ToList();

            if (automatic.Count > 0)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{Option} names {Quoted(automatic)}, which {(automatic.Count == 1 ? "is" : "are")} not manual: a run "
                    + $"of runner '{runnerName}' that names no step runs {(automatic.Count == 1 ? "it" : "them")} already. "
                    + $"{char.ToUpperInvariant(available[0])}{available[1..]}.");
            }

            named = ManualSteps;
        }
        else if (RunnerSteps.Count > 0)
        {
            var unknown = RunnerSteps.Where(name => !declared.Contains(name, StringComparer.Ordinal)).ToList();

            if (unknown.Count > 0)
            {
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"Runner '{runnerName}' names {Quoted(unknown)} under steps, which '{file.Path}' does not declare. "
                    + $"It declares {string.Join(", ", declared)}.");
            }

            named = RunnerSteps;
        }
        else
        {
            named = [.. file.Steps.Where(step => !step.Manual).Select(step => step.Name)];

            // Every step manual, and none named: a run of this runner would run nothing and pass.
            if (named.Count == 0)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"Every step of '{file.Path}' is manual, and runner '{runnerName}' names none under steps, so a run "
                    + $"that names no step would run nothing. Name one with {Option}: {string.Join(", ", manual)}.");
            }
        }

        var chosen = WithWhatTheyNeed(file, named);

        // In the order declared, whatever order they were named in: steps run in the order the file
        // declares them, and what a step needs is always declared before it.
        var selected = file.Steps.Where(step => chosen.Contains(step.Name)).ToList();

        return new SelectedSteps(
            file with { Steps = selected },
            [.. file.Steps.Where(step => !chosen.Contains(step.Name)).Select(step => step.Name)],
            [.. selected.Where(step => step.Manual).Select(step => step.Name)])
        {
            Declared = file,
            Named = ManualSteps.Count > 0 ? ManualSteps : RunnerSteps,
        };
    }

    /// <summary><paramref name="named"/>, and every step any of them needs, however deep.</summary>
    private static HashSet<string> WithWhatTheyNeed(ActionFile file, IReadOnlyList<string> named)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(named);

        while (pending.TryPop(out var name))
        {
            if (!chosen.Add(name))
            {
                continue;
            }

            // The parser holds 'needs' to steps declared before, so this always finds one and never
            // goes round in a circle.
            foreach (var step in file.Steps.Where(step => string.Equals(step.Name, name, StringComparison.Ordinal)))
            {
                foreach (var needed in step.Needs)
                {
                    pending.Push(needed);
                }
            }
        }

        return chosen;
    }

    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"'{name}'"));
}

/// <summary>An action's steps as one run runs them.</summary>
/// <param name="File">The action holding only the steps the run selected, in the order they are declared.</param>
/// <param name="Unselected">The steps it did not select, by name, in the order they are declared.</param>
/// <param name="Manual">The manual steps it did select, by name, in the order they are declared.</param>
public sealed record SelectedSteps(ActionFile File, IReadOnlyList<string> Unselected, IReadOnlyList<string> Manual)
{
    /// <summary>The action as it was read, every step included: what a refusal about the others reads.</summary>
    public required ActionFile Declared { get; init; }

    /// <summary>
    /// The steps the run named - with <c>--manual-step</c>, or as the runner's own <c>steps</c> - and
    /// empty for a run that named none.
    /// </summary>
    public IReadOnlyList<string> Named { get; init; } = [];

    /// <summary>
    /// Refuses every leg on whose operating system none of the steps the run named runs.
    /// </summary>
    /// <param name="runnerName">The runner, as the refusal names it.</param>
    /// <param name="legs">Each leg the run reaches, by name, with its operating system.</param>
    /// <exception cref="HarnessException">Some leg runs none of the steps the run named; every such leg is named.</exception>
    /// <remarks>
    /// Such a leg runs only what the named steps need, and passes: a benchmark named for a system that
    /// leg is not ran nowhere, and the run said passed. A run that named no step runs what every run
    /// runs, and is asked only what <see cref="ActionFile.RequireAStepOn"/> asks.
    /// </remarks>
    public void RequireANamedStepOn(string runnerName, IEnumerable<(string Leg, string Os)> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);

        if (Named.Count == 0)
        {
            return;
        }

        var named = File.Steps.Where(step => Named.Contains(step.Name, StringComparer.Ordinal)).ToList();
        var idle = legs.Where(leg => !named.Any(step => step.RunsOn(leg.Os))).ToList();

        if (idle.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"Runner '{runnerName}' runs none of the steps this run names - "
                + $"{string.Join(", ", named.Select(step => step.RunOn.Count == 0 ? $"'{step.Name}'" : $"'{step.Name}' (runs on {string.Join(", ", step.RunOn)})"))} - "
                + $"on the operating system of {string.Join(", ", idle.Select(leg => $"leg '{leg.Leg}' ({leg.Os})"))}, so it "
                + "would run only what they need there and pass. Leave that leg out of the run, or name a step that runs there.");
        }
    }
}
