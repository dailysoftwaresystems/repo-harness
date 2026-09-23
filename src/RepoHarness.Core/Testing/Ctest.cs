namespace RepoHarness.Core.Testing;

/// <summary>
/// What ctest makes of the options a test invocation gives it, where that decides what the harness
/// must do: the options <c>init</c> seeds for a CMake project, and how ctest combines an option that
/// chooses its tests with another of the same kind - given twice, or set by a test preset.
/// </summary>
internal static class Ctest
{
    /// <summary>ctest's program name.</summary>
    public const string Program = "ctest";

    /// <summary>Chooses the tests to run by name.</summary>
    public const string FilterArg = "-R";

    /// <summary>Leaves out the tests a label matches.</summary>
    public const string ExcludeArg = "-LE";

    /// <summary>Chooses the tests to run by a label they carry.</summary>
    public const string LabelArg = "-L";

    /// <summary>
    /// What joins regular expressions into one that matches what any of them does: several exclusions,
    /// given to ctest as one, each leave their tests out.
    /// </summary>
    public const string ExcludeJoin = "|";

    /// <summary>The option naming a test preset.</summary>
    private const string PresetArg = "--preset";

    /// <summary>The spellings of the option that takes the union of the tests -I and -R choose.</summary>
    private static readonly string[] UnionArgs = ["-U", "--union"];

    /// <summary>
    /// ctest's options that choose its tests by name or by label. Measured with ctest 4.3.2: given one
    /// of them more than once, in any of its spellings, it keeps only the last where it names tests, and
    /// runs or leaves out only a test every one matches where it names labels; and a test preset that sets
    /// the same filter is combined with it the same way. Beside --union given in its args, whatever its
    /// value, it reads -E alone.
    /// </summary>
    private static readonly Selection[] Selections =
    [
        new(["-R", "--tests-regex"], "include.name", Narrows: false, ReadBesideUnion: false),
        new(["-E", "--exclude-regex"], "exclude.name", Narrows: false, ReadBesideUnion: true),
        new(["-L", "--label-regex"], "include.label", Narrows: true, ReadBesideUnion: false),
        new(["-LE", "--label-exclude"], "exclude.label", Narrows: true, ReadBesideUnion: false),
    ];

    /// <summary>
    /// The option <paramref name="argument"/> is to ctest, where <paramref name="runner"/> is ctest and
    /// the argument one of the options that choose its tests; <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="argument">The argument an invocation introduces a filter, an exclusion or a label with.</param>
    public static Selection? SelectionOf(string runner, string? argument)
        => IsCtest(runner) ? Selections.FirstOrDefault(selection => selection.Spellings.Contains(argument, StringComparer.Ordinal)) : null;

    /// <summary>
    /// The test preset <paramref name="args"/> name, where <paramref name="runner"/> is ctest; <see langword="null"/>
    /// otherwise.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="args">The invocation's own arguments.</param>
    /// <remarks>
    /// Given after the option or after '=', as ctest takes it; ctest takes a preset given after a space in
    /// the same argument for no option at all, and of two presets uses the first.
    /// </remarks>
    public static string? PresetIn(string runner, IReadOnlyList<string> args)
    {
        for (var index = 0; IsCtest(runner) && index < args.Count; index++)
        {
            if (string.Equals(args[index], PresetArg, StringComparison.Ordinal) && index + 1 < args.Count)
            {
                return args[index + 1];
            }

            if (args[index].StartsWith(PresetArg + "=", StringComparison.Ordinal))
            {
                return args[index][(PresetArg.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="runner"/> is ctest and <paramref name="args"/> ask it to rerun the tests that
    /// failed last time: measured with ctest 4.3.2, it then passes over -R, -L and -LE, and -E makes it run
    /// other tests than those.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="args">The invocation's own arguments.</param>
    public static bool RerunsFailed(string runner, IReadOnlyList<string> args)
        => IsCtest(runner) && args.Contains("--rerun-failed", StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="runner"/> is ctest and <paramref name="args"/> give it --union, in either
    /// spelling and any form. Measured with ctest 4.3.2, it takes a value, and given any - OFF and 0 among
    /// them - runs every test -R, -L and -LE would leave out, reading -E alone; given none, it refuses to run.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="args">The invocation's own arguments.</param>
    public static bool Unites(string runner, IReadOnlyList<string> args)
        => IsCtest(runner)
            && (Values(args, UnionArgs).Values.Count > 0 || args.Any(argument => UnionArgs.Contains(argument, StringComparer.Ordinal)));

    /// <summary>
    /// The values <paramref name="args"/> give an option spelled any of <paramref name="spellings"/> - after it,
    /// after it and '=', or after it and a space in the same argument, each of which ctest takes - and the args
    /// without them.
    /// </summary>
    /// <param name="args">An invocation's own arguments.</param>
    /// <param name="spellings">The option's spellings.</param>
    public static (IReadOnlyList<string> Values, IReadOnlyList<string> Others) Values(IReadOnlyList<string> args, IReadOnlyList<string> spellings)
    {
        var given = Given(args, spellings).ToList();
        var taken = given
            .SelectMany(value => value.Prefix.Length == 0 ? new[] { value.Index - 1, value.Index } : [value.Index])
            .ToHashSet();

        return ([.. given.Select(value => value.Value)], [.. args.Where((_, index) => !taken.Contains(index))]);
    }

    /// <summary>
    /// <paramref name="args"/> with <paramref name="suffix"/> after each value they give an option spelled any of
    /// <paramref name="spellings"/> - or after the last alone - each in the form it was given.
    /// </summary>
    /// <param name="args">An invocation's own arguments.</param>
    /// <param name="spellings">The option's spellings.</param>
    /// <param name="suffix">What to add after each value.</param>
    /// <param name="lastOnly">Whether only the last value takes it.</param>
    public static IReadOnlyList<string> Appended(IReadOnlyList<string> args, IReadOnlyList<string> spellings, string suffix, bool lastOnly)
    {
        var given = Given(args, spellings).ToList();
        var appended = args.ToList();

        foreach (var (index, prefix, value) in lastOnly ? given.TakeLast(1) : given)
        {
            appended[index] = prefix + value + suffix;
        }

        return appended;
    }

    /// <summary>
    /// Each value <paramref name="args"/> give an option spelled any of <paramref name="spellings"/>: the argument
    /// holding it, what comes before it in that argument, and the value.
    /// </summary>
    private static IEnumerable<(int Index, string Prefix, string Value)> Given(IReadOnlyList<string> args, IReadOnlyList<string> spellings)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];

            if (spellings.Contains(argument, StringComparer.Ordinal) && index + 1 < args.Count)
            {
                index++;
                yield return (index, string.Empty, args[index]);
            }
            else if (spellings.FirstOrDefault(spelling => argument.StartsWith(spelling + "=", StringComparison.Ordinal)
                || argument.StartsWith(spelling + " ", StringComparison.Ordinal)) is { } spelling)
            {
                yield return (index, argument[..(spelling.Length + 1)], argument[(spelling.Length + 1)..]);
            }
        }
    }

    /// <summary>Whether <paramref name="runner"/> is ctest, by its program's name, however its path is spelled.</summary>
    private static bool IsCtest(string runner)
        => string.Equals(Path.GetFileNameWithoutExtension(runner), Program, StringComparison.OrdinalIgnoreCase);

    /// <summary>One of ctest's options that choose its tests.</summary>
    /// <param name="Spellings">Its spellings.</param>
    /// <param name="PresetFilter">The filter a test preset sets it with, under <c>filter</c>: <c>include.name</c> and the like.</param>
    /// <param name="Narrows">
    /// Whether ctest, given it twice, reads both - running or leaving out only a test every one matches - rather
    /// than keeping the last.
    /// </param>
    /// <param name="ReadBesideUnion">Whether ctest still reads it beside --union given in its args.</param>
    public sealed record Selection(IReadOnlyList<string> Spellings, string PresetFilter, bool Narrows, bool ReadBesideUnion);
}
