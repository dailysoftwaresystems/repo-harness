using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runners;

/// <summary>
/// The values <c>run --input name=value</c> gives an action's inputs: over what the runner value
/// directories hold and each input's own default, for this run alone.
/// </summary>
/// <remarks>
/// A value on a command line is a plain one. It reaches the process table and the shell's history,
/// so a secret still belongs in <c>.secrets</c>, which reaches a step through its environment and is
/// never printed.
/// </remarks>
public static class CommandLineInputs
{
    /// <summary>The option that gives one input its value.</summary>
    public const string Option = "--input";

    /// <summary>Reads each <c>name=value</c> given, in the order given.</summary>
    /// <param name="pairs">What each <c>--input</c> was given.</param>
    /// <exception cref="HarnessException">
    /// A pair has no <c>=</c>, no name or no value, or a name is given twice. Every problem is named
    /// together, before anything is read or connected to.
    /// </exception>
    /// <remarks>
    /// Split at the first <c>=</c>, so a value may hold one. An empty value is refused rather than
    /// given: <c>--input root="$ROOT"</c> with the variable never set is not a request to run with
    /// nothing, and read as one it would override the value the runner's .env holds. A name given
    /// twice is refused rather than letting the later win, as a name defined twice in the value
    /// directories is: which value ran would depend on where it sat on the line.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Parse(IReadOnlyList<string> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                problems.Add(separator < 0
                    ? $"{Option} '{pair}' is not name=value"
                    : $"{Option} '{pair}' names no input");
                continue;
            }

            var name = pair[..separator];

            if (separator == pair.Length - 1)
            {
                problems.Add($"{Option} '{pair}' gives '{name}' no value; leave it out to use the runner's .env value or the input's default");
                continue;
            }

            if (!given.TryAdd(name, pair[(separator + 1)..]))
            {
                problems.Add($"{Option} gives '{name}' a value more than once");
            }
        }

        return problems.Count == 0
            ? given
            : throw new HarnessException(HarnessExit.UsageError, string.Join("; ", problems.Distinct(StringComparer.Ordinal)) + ".");
    }

    /// <summary>
    /// Refuses a value given to an input <paramref name="file"/> does not declare - to any input at
    /// all, where the runner runs its own phases and so has no file.
    /// </summary>
    /// <param name="runnerName">The runner, as the refusal names it.</param>
    /// <param name="file">The runner's action, or <see langword="null"/> for a runner of phases.</param>
    /// <param name="given">What <see cref="Parse"/> read.</param>
    /// <exception cref="HarnessException">A name is not an input the action declares.</exception>
    /// <remarks>
    /// Declared inputs only, matched exactly as a run line names them. A value for a name the file
    /// never reads changes nothing, so accepting it would let a mistyped name run the action on its
    /// default while the command line said otherwise.
    /// </remarks>
    public static void RequireDeclared(string runnerName, ActionFile? file, IReadOnlyDictionary<string, string> given)
        => RequireRead(runnerName, file is null ? null : new SelectedSteps(file, [], []) { Declared = file }, given);

    /// <summary>
    /// Refuses a value given to an input no step of <paramref name="selected"/> reads: neither one the
    /// action declares for every step, nor one a step this run runs declares for itself.
    /// </summary>
    /// <param name="runnerName">The runner, as the refusal names it.</param>
    /// <param name="selected">The steps the run runs, or <see langword="null"/> for a runner of phases.</param>
    /// <param name="given">What <see cref="Parse"/> read.</param>
    /// <exception cref="HarnessException">A name is not an input a step this run runs reads.</exception>
    /// <remarks>
    /// An input of a step the run leaves out is refused as surely as a mistyped one, and said as that:
    /// given on a plain run, a manual step's input would change nothing while the command line read as
    /// though the step had run with it.
    /// </remarks>
    public static void RequireRead(string runnerName, SelectedSteps? selected, IReadOnlyDictionary<string, string> given)
    {
        ArgumentNullException.ThrowIfNull(given);

        if (given.Count == 0)
        {
            return;
        }

        if (selected is null)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"Runner '{runnerName}' runs phases of its own, which declare no inputs, so {Option} has nothing to give a value to.");
        }

        var file = selected.File;
        var declared = file.Inputs.Select(input => input.Name)
            .Concat(file.Steps.SelectMany(step => step.Inputs).Select(input => input.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = given.Keys.Where(name => !declared.Contains(name, StringComparer.Ordinal)).ToList();

        if (unknown.Count == 0)
        {
            return;
        }

        // An input of a step this run leaves out, said as that, and apart from a name nothing declares.
        var leftOut = unknown
            .Select(name => (Name: name, Steps: selected.Declared.Steps
                .Where(step => !file.Steps.Contains(step) && step.Inputs.Any(input => string.Equals(input.Name, name, StringComparison.Ordinal)))
                .Select(step => step.Name)
                .ToList()))
            .Where(entry => entry.Steps.Count > 0)
            .ToList();

        var unread = unknown.Where(name => leftOut.All(entry => entry.Name != name)).ToList();
        var said = new List<string>();

        if (unread.Count > 0)
        {
            said.Add($"{Option} names {string.Join(", ", unread.Select(name => $"'{name}'"))}, which '{file.Path}' does not "
                + "declare under inputs");
        }

        foreach (var (name, steps) in leftOut)
        {
            said.Add($"{Option} names '{name}', which only step(s) {string.Join(", ", steps.Select(step => $"'{step}'"))} "
                + $"read{(steps.Count == 1 ? "s" : string.Empty)}, and this run of runner '{runnerName}' does not run "
                + $"{(steps.Count == 1 ? "it" : "them")}");
        }

        // Said as the file declares them where no step declares its own, as it always was; where one does,
        // what this run's steps read, since that is what a value can reach.
        var stepsDeclareTheirOwn = selected.Declared.Steps.Any(step => step.Inputs.Count > 0);

        throw new HarnessException(
            HarnessExit.UsageError,
            $"{string.Join("; ", said)}; "
            + (stepsDeclareTheirOwn
                ? declared.Count == 0 ? "the steps this run runs read none." : $"the steps this run runs read {string.Join(", ", declared)}."
                : declared.Count == 0 ? "it declares none." : $"it declares {string.Join(", ", declared)}."));
    }

    /// <summary>The arguments that give <paramref name="given"/> again, for a host running one of this run's legs.</summary>
    /// <param name="given">What <see cref="Parse"/> read.</param>
    public static IEnumerable<string> Arguments(IReadOnlyDictionary<string, string> given)
    {
        ArgumentNullException.ThrowIfNull(given);

        return given.SelectMany(pair => new[] { Option, $"{pair.Key}={pair.Value}" });
    }
}
