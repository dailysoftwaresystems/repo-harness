using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Testing;

/// <summary>
/// Works out how one leg runs its tests, from the project's settings, the leg's own, and the
/// platform it runs on.
/// </summary>
/// <remarks>
/// Three layers, each more particular than the last: the project's settings; a leg's own, which replace
/// them for a leg that runs another suite, a sanitizer's subset say; and an operating system's section,
/// merged over the shared one. What a leg on a host's copy leaves out beside the rest is the invocation's
/// remoteExcludes. Resolving them in one place is what keeps the runner, the success pattern and the
/// exclusions from being decided by three different rules.
/// </remarks>
public static class TestInvocationResolver
{
    /// <summary>The test settings a leg uses, or null when neither it nor its project declares any.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="project">The project the leg builds.</param>
    public static TestConfig? SettingsFor(HarnessConfig config, LegConfig leg, ProjectConfig? project)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);

        // The leg's own settings replace the project's rather than merging with them: a leg that
        // declares a test section is saying what it runs, and a half-inherited runner is how a leg
        // ends up running the project's suite with its own arguments.
        return leg.Test ?? project?.Test;
    }

    /// <summary>
    /// The invocation for one platform: the platform's own section merged field by field over
    /// <c>all</c>.
    /// </summary>
    /// <param name="settings">The test settings.</param>
    /// <param name="platformKey">The operating system the leg runs on.</param>
    /// <exception cref="HarnessException">
    /// The merged invocation names no runner, or no success pattern. Both are refused rather than
    /// defaulted: a run with no runner does nothing, and a run with no pattern reports a zero exit
    /// code as a pass, which is indistinguishable from never having run.
    /// </exception>
    public static ResolvedTestInvocation Resolve(TestConfig settings, string platformKey)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var merged = Merge(settings.All, SectionFor(settings, platformKey));

        if (string.IsNullOrWhiteSpace(merged.Runner))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"The test settings name no runner on {platformKey}, so there is nothing to run.");
        }

        if (string.IsNullOrWhiteSpace(merged.SuccessPattern))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"The test settings name no successPattern on {platformKey}. A zero exit code is not "
                + "proof a suite ran: a wrapper that reports success without evidence cannot be told "
                + "apart from one that never ran.");
        }

        return merged;
    }

    /// <summary>
    /// The command line for a resolved invocation, with a filter and exclusions spliced in - the caller's,
    /// and on a leg a host runs the invocation's remoteExcludes beside them - and the core count handed
    /// over explicitly.
    /// </summary>
    /// <param name="invocation">The resolved invocation.</param>
    /// <param name="cores">The core count this leg runs with.</param>
    /// <param name="filter">A filter the caller asked for, or null.</param>
    /// <param name="excludes">Exclusions to give the runner: the caller's, and any remoteExcludes beside them.</param>
    /// <param name="paths">
    /// The directories this leg runs against, which the arguments and the working directory may
    /// name. Null is for a caller with no leg in hand: every placeholder is left as written and the
    /// configured working directory is dropped rather than resolved, so what comes back describes
    /// the shape of the command and not a command anybody should run.
    /// </param>
    /// <param name="labels">Labels the tests to run must carry, as the caller asked for them.</param>
    /// <param name="fileSystem">What a ctest test preset the args name is read through, where one must be read.</param>
    /// <remarks>
    /// The core count goes through <c>coresEnv</c> where the runner reads a variable and
    /// <c>coresArgs</c> where it does not. A variable is preferred because an explicit option in the
    /// invocation's own arguments still wins, decided by the runner itself, where a spliced-in
    /// option would contradict it and the runner would pick one without saying which.
    /// <para>
    /// The filter chooses the tests to run, each exclusion leaves its tests out and each label is one
    /// more the tests to run must carry, whatever else the invocation chooses them by. ctest reads each
    /// of its options that choose tests beside another of the same kind - given twice, or set by a test
    /// preset - keeping only the last where it names tests, and taking only a test every one matches
    /// where it names labels. So exclusions are added to the args' own where they stand (see
    /// <see cref="Excluding"/>); a filter beside a -R the args give, and a filter or an exclusion beside a
    /// preset that sets the same filter, is refused rather than run on a selection nobody asked for. A
    /// label is not, since ctest reads it as one more a test must carry, which is what it asks. Beside
    /// what voids the options, any of the three is refused: --rerun-failed, --union in the args - which
    /// leaves ctest reading -E alone - a preset that takes a union, and a preset that could not be read
    /// to say what it chooses.
    /// </para>
    /// </remarks>
    public static TestCommand CommandFor(
        ResolvedTestInvocation invocation,
        int cores,
        string? filter,
        IReadOnlyList<string>? excludes,
        LegPaths? paths = null,
        IReadOnlyList<string>? labels = null,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var workingDirectory = invocation.WorkingDirectory is { Length: > 0 } declared && paths is not null
            ? Rooted(LegPathNames.Expand(declared, paths, "test.workingDirectory"), paths.TreeRoot)
            : null;

        // A test preset the args name, read from the directory ctest starts in where an option beside it asks.
        var preset = new PresetBeside(invocation, fileSystem, paths, workingDirectory);

        RefuseBlank(filter is null ? [] : [filter], "A filter");
        RefuseBlank(excludes ?? [], "An exclusion");
        RefuseBlank(labels ?? [], "A label");

        var (args, exclusions) = Excluding(invocation, excludes ?? [], preset);
        var arguments = new List<string>(args);

        if (filter is not null)
        {
            if (string.IsNullOrWhiteSpace(invocation.FilterArg))
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    "A filter was given, but the test settings declare no filterArg, so nothing knows "
                    + "how to pass one to this runner.");
            }

            var option = Ctest.SelectionOf(invocation.Runner, invocation.FilterArg);

            RefuseBeside(invocation, preset, option, "A filter", sameFilterRefuses: true);

            if (option is not null && Ctest.Values(invocation.Args, option.Spellings).Values.Count > 0)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"A filter was given, and the test settings' args give {invocation.FilterArg} already, of which ctest "
                    + $"{Twice(option)}: the filter would not choose the tests it names among theirs. Give it in the args, or "
                    + "take theirs out.");
            }

            arguments.Add(invocation.FilterArg);
            arguments.Add(filter);
        }

        foreach (var exclude in exclusions)
        {
            arguments.Add(invocation.ExcludeArg!);
            arguments.Add(exclude);
        }

        if (labels is { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(invocation.LabelArg))
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    "A label was given, but the test settings declare no labelArg, so nothing knows how "
                    + "to pass one to this runner.");
            }

            // Beside a preset's own labels ctest reads each as one more a test must carry, which is what a label asks.
            RefuseBeside(invocation, preset, Ctest.SelectionOf(invocation.Runner, invocation.LabelArg), "A label", sameFilterRefuses: false);

            foreach (var label in labels)
            {
                arguments.Add(invocation.LabelArg);
                arguments.Add(label);
            }
        }

        // Through CoreCounts rather than spelled again here. A second implementation of the same
        // rule is how an explicit '-j 2' in args ends up beside a spliced '-j 6', with the runner
        // picking one and the report saying which it asked for rather than which it got.
        arguments.AddRange(CoreCounts.Arguments(invocation.CoresArgs, cores, invocation.Args));

        var environment = new Dictionary<string, string>(invocation.Env, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in CoreCounts.Environment(invocation.CoresEnv, cores))
        {
            if (value is not null)
            {
                environment[name] = value;
            }
        }

        // Expanded once, here, after the filter and the exclusions have been spliced in and the
        // core count added: every argument the runner will see goes through one rule, so an
        // argument that arrived from --filter cannot name a directory an argument from config
        // could not.
        if (paths is not null)
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                arguments[index] = LegPathNames.Expand(arguments[index], paths, "test.args");
            }
        }

        return new TestCommand(invocation.Runner, arguments, environment, workingDirectory);
    }

    /// <summary>
    /// Refuses <paramref name="values"/> where any is empty, or spaces alone. Measured with ctest 4.3.2, an empty
    /// one alone is read as not given at all, and joined with another it matches everything; and spaces alone are
    /// a value lost on the way far more often than a pattern, which can spell a space <c>[ ]</c>.
    /// </summary>
    private static void RefuseBlank(IReadOnlyList<string> values, string what)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{what} was given empty, or as spaces alone: a runner reads an empty one as not given, or, joined with "
                + "another, as matching everything, and spaces alone are a value lost on the way far more often than a "
                + "pattern, which can say '[ ]'. Give each a value.");
        }
    }

    /// <summary>
    /// Refuses <paramref name="what"/>, given as <paramref name="option"/>, where ctest would not choose the tests
    /// it names: beside <c>--rerun-failed</c>, which passes over -R, -L and -LE and given -E runs other tests than
    /// the ones that failed; beside <c>--union</c> in the args, where the option is one ctest then passes over; and
    /// beside a test preset that takes the union of what it chooses, that sets the same filter itself where
    /// <paramref name="sameFilterRefuses"/>, or that could not be read to say.
    /// </summary>
    private static void RefuseBeside(ResolvedTestInvocation invocation, PresetBeside preset, Ctest.Selection? option, string what, bool sameFilterRefuses)
    {
        if (Ctest.RerunsFailed(invocation.Runner, invocation.Args))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{what} was given, and the test settings run ctest with --rerun-failed, which - measured with ctest 4.3.2 - "
                + "passes over -R, -L and -LE, and given -E runs other tests than the ones that failed. Run without "
                + "--rerun-failed.");
        }

        if (option is null)
        {
            return;
        }

        if (!option.ReadBesideUnion && Ctest.Unites(invocation.Runner, invocation.Args))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{what} was given as {option.Spellings[0]}, and the test settings run ctest with --union, beside which - "
                + "measured with ctest 4.3.2, whatever value it is given - ctest runs every test -R, -L and -LE would "
                + "leave out, reading -E alone. Take --union out of the args.");
        }

        preset.Refuse(option, what, sameFilterRefuses);
    }

    /// <summary>What ctest makes of one of its options given twice, to end a sentence.</summary>
    private static string Twice(Ctest.Selection option)
        => option.Narrows ? "reads each as one more that a test must match" : "keeps only the last";

    /// <summary>
    /// The invocation's args, and the exclusions to add after them, each after an excludeArg of its own.
    /// </summary>
    /// <param name="invocation">The resolved invocation.</param>
    /// <param name="given">The exclusions the caller asked for.</param>
    /// <param name="preset">A ctest test preset the args name.</param>
    /// <remarks>
    /// Each exclusion given leaves its tests out, whatever else leaves tests out beside it; where none is
    /// given, the args are the author's own, run as written. Several given reach a runner that declares an
    /// <c>excludeJoin</c> as one value joined by it.
    /// <para>
    /// ctest reads each value its args already give the option beside those given: as one more a test must
    /// match where it names labels, and keeping the last where it names tests. So each value the args give
    /// -LE, in any spelling and form, takes the exclusions given as one more alternative - "(A and B) or C"
    /// is "(A or C) and (B or C)", measured with ctest 4.3.2 - and the last value they give -E alone takes
    /// them, the others being read by nothing already. Written in place, each as the author spelled it.
    /// Beside another given apart with no join to give them as one, ctest is refused rather than run on a
    /// selection nobody asked for, and so it is beside a test preset that leaves tests out the same way.
    /// </para>
    /// <para>
    /// Another runner's reading of its option given twice is its own: where it declares a join, the values
    /// its args give are lifted out and joined with those given, as the exclusions they are.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<string> Args, IReadOnlyList<string> Exclusions) Excluding(
        ResolvedTestInvocation invocation,
        IReadOnlyList<string> given,
        PresetBeside preset)
    {
        if (given.Count == 0)
        {
            return (invocation.Args, []);
        }

        if (string.IsNullOrWhiteSpace(invocation.ExcludeArg))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                "An exclusion was given, but the test settings declare no excludeArg, so nothing "
                + "knows how to pass one to this runner.");
        }

        var join = invocation.ExcludeJoin is { Length: > 0 } declared ? declared : null;
        var option = Ctest.SelectionOf(invocation.Runner, invocation.ExcludeArg);
        var (own, others) = Ctest.Values(invocation.Args, option?.Spellings ?? [invocation.ExcludeArg]);

        if (own.Count + given.Count > 1 && join is null && option is not null)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{own.Count + given.Count} exclusions would reach ctest apart, each after {invocation.ExcludeArg}, of which it "
                + $"{Twice(option)} - never what each leaves out. Declare \"excludeJoin\": \"{Ctest.ExcludeJoin}\" in the test "
                + "settings, so they reach it as one regular expression.");
        }

        if (own.Count + given.Count <= 1 || join is null)
        {
            RefuseBeside(invocation, preset, option, "An exclusion", sameFilterRefuses: true);

            return (invocation.Args, given);
        }

        // Joined with those given, an empty one matches every name or label, and would leave out every test.
        RefuseBlank(own, "An exclusion in the test settings' args");

        if (option is null)
        {
            return (others, [string.Join(join, own.Concat(given))]);
        }

        RefuseBeside(invocation, preset, option, "An exclusion", sameFilterRefuses: true);

        if (own.Count == 0)
        {
            return (invocation.Args, [string.Join(join, given)]);
        }

        return (Ctest.Appended(invocation.Args, option.Spellings, join + string.Join(join, given), lastOnly: !option.Narrows), []);
    }

    /// <summary>
    /// A ctest test preset an invocation's args name, its placeholders filled in, read - once, and only where an
    /// option given beside it asks - from the directory ctest starts in.
    /// </summary>
    private sealed class PresetBeside(ResolvedTestInvocation invocation, IFileSystem? fileSystem, LegPaths? paths, string? workingDirectory)
    {
        private readonly string? _name = Named(invocation, paths);

        private (IReadOnlySet<string>? Filters, string? Unread)? _read;

        /// <summary>The preset the args name, its placeholders filled in as every other argument's are, where the leg is in hand.</summary>
        private static string? Named(ResolvedTestInvocation invocation, LegPaths? paths)
            => Ctest.PresetIn(invocation.Runner, invocation.Args) is { } written && paths is not null
                ? LegPathNames.Expand(written, paths, "test.args")
                : Ctest.PresetIn(invocation.Runner, invocation.Args);

        /// <summary>
        /// Refuses <paramref name="what"/>, given as <paramref name="option"/>, where the preset takes the union of
        /// the tests its filters choose - which voids every option that chooses tests - where it sets the same filter
        /// itself and <paramref name="sameFilterRefuses"/>, or where it could not be read to say. ctest combines a
        /// preset's filter with the command line's so that neither chooses the tests it names: measured, a preset's
        /// label exclusion and -LE leave out only a test both match, and -E and -R replace the preset's.
        /// </summary>
        public void Refuse(Ctest.Selection option, string what, bool sameFilterRefuses)
        {
            if (_name is null)
            {
                return;
            }

            _read ??= fileSystem is null || paths is null
                ? (null, "nothing here reads the preset files")
                : CtestPresets.FiltersOf(fileSystem, StartDirectory(workingDirectory, paths.TreeRoot), _name);

            var (filters, unread) = _read.Value;

            if (filters is null)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{what} was given, and the test settings run ctest with test preset '{_name}', which could not be read to say "
                    + $"what it chooses itself: {unread}. Declare it in the preset, or run ctest without one.");
            }

            if (filters.Contains(CtestPresets.Union))
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{what} was given, and the test settings run ctest with test preset '{_name}', which takes the union of the "
                    + "tests its filters choose: measured, ctest then runs tests none of -R, -L, -E and -LE would. Declare it in "
                    + "the preset, or run ctest without one.");
            }

            if (sameFilterRefuses && filters.Contains(option.PresetFilter))
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{what} was given, and the test settings run ctest with test preset '{_name}', which sets "
                    + $"filter.{option.PresetFilter} itself; given that and {option.Spellings[0]}, ctest {Twice(option)}. Declare "
                    + "it in the preset, or run ctest without one.");
            }
        }
    }

    /// <summary>
    /// The directory a test runner starts in: <paramref name="workingDirectory"/>, the command's own, made
    /// whole against <paramref name="treeRoot"/>, or the tree root where it names none. Made whole on the
    /// machine that starts the runner, so one rooted but not whole - '	ests' on Windows - is on the tree's
    /// own drive, never on whichever drive this process happens to be on.
    /// </summary>
    /// <param name="workingDirectory">The command's working directory, as <see cref="CommandFor"/> gives it.</param>
    /// <param name="treeRoot">The leg's tree root.</param>
    public static string StartDirectory(string? workingDirectory, string treeRoot)
        => Path.GetFullPath(workingDirectory ?? treeRoot, treeRoot);

    /// <summary>
    /// <paramref name="path"/> read against <paramref name="treeRoot"/> when it is relative, and as
    /// written when it is already rooted - which on Windows may still lack a drive, for the machine
    /// that starts the runner to make whole.
    /// </summary>
    /// <remarks>
    /// So a working directory can be written either way: <c>{buildDir}</c> expands to an absolute
    /// path already, while a plain <c>tests/integration</c> means what every other path in the
    /// configuration means, which is somewhere under the tree.
    /// </remarks>
    private static string Rooted(string path, string treeRoot)
        => Path.IsPathRooted(path) ? path : Path.Combine(treeRoot, path);

    /// <summary>
    /// The invocation a leg's tests use on <paramref name="platformKey"/>: its runner - empty when the
    /// settings name none there - and the environment that runner starts in.
    /// </summary>
    /// <param name="settings">The test settings.</param>
    /// <param name="platformKey">The operating system the leg runs on.</param>
    /// <remarks>
    /// Read by the same merge <see cref="Resolve"/> uses, and without its refusals: a survey asks
    /// which program a host must have, and settings that name none are the test command's to refuse,
    /// in its own words, when somebody runs it.
    /// </remarks>
    public static ResolvedTestInvocation InvocationFor(TestConfig settings, string platformKey)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Merge(settings.All, SectionFor(settings, platformKey));
    }

    /// <summary>The section of <paramref name="settings"/> for one operating system, if it has one.</summary>
    private static TestInvocation? SectionFor(TestConfig settings, string platformKey) => platformKey switch
    {
        PlatformNames.Windows => settings.Windows,
        PlatformNames.Linux => settings.Linux,
        PlatformNames.MacOs => settings.Macos,
        _ => null,
    };

    private static ResolvedTestInvocation Merge(TestInvocation? all, TestInvocation? platform)
        => new(
            platform?.Runner ?? all?.Runner ?? string.Empty,
            platform?.Args ?? all?.Args ?? [],
            platform?.FilterArg ?? all?.FilterArg,
            platform?.ExcludeArg ?? all?.ExcludeArg,
            platform?.ExcludeJoin ?? all?.ExcludeJoin,
            platform?.LabelArg ?? all?.LabelArg,
            platform?.Cores ?? all?.Cores,
            platform?.CoresArgs ?? all?.CoresArgs ?? [],
            platform?.CoresEnv ?? all?.CoresEnv ?? [],
            platform?.Env ?? all?.Env ?? [],
            platform?.SuccessPattern ?? all?.SuccessPattern ?? string.Empty,
            platform?.CountPattern ?? all?.CountPattern,
            platform?.WorkingDirectory ?? all?.WorkingDirectory,
            platform?.TestSet ?? all?.TestSet,
            platform?.RemoteExcludes ?? all?.RemoteExcludes ?? []);
}

/// <summary>One test invocation, with every field decided.</summary>
/// <param name="Runner">The test runner to start.</param>
/// <param name="Args">Its arguments.</param>
/// <param name="FilterArg">The argument that introduces a filter, or null.</param>
/// <param name="ExcludeArg">The argument that introduces an exclusion, or null.</param>
/// <param name="ExcludeJoin">What joins several exclusions into one value after it; null or empty to give each its own.</param>
/// <param name="LabelArg">The argument that introduces a label the tests to run must carry, or null.</param>
/// <param name="Cores">The core count this invocation asks for, or null to take the host's.</param>
/// <param name="CoresArgs">Arguments carrying the core count, with <c>{cores}</c> to replace.</param>
/// <param name="CoresEnv">Variables to set to the core count.</param>
/// <param name="Env">Environment for the invocation.</param>
/// <param name="SuccessPattern">What must appear in the runner's own output for this to pass.</param>
/// <param name="CountPattern">What captures how many tests ran, or null.</param>
/// <param name="WorkingDirectory">Where the runner starts, or null for the leg's tree root.</param>
/// <param name="TestSet">Which of the project's test sets it runs, or null for the shared one.</param>
/// <param name="RemoteExcludes">What a leg on a host reached through a transport leaves out beside what it is asked to.</param>
public sealed record ResolvedTestInvocation(
    string Runner,
    IReadOnlyList<string> Args,
    string? FilterArg,
    string? ExcludeArg,
    string? ExcludeJoin,
    string? LabelArg,
    int? Cores,
    IReadOnlyList<string> CoresArgs,
    IReadOnlyList<string> CoresEnv,
    IReadOnlyDictionary<string, string> Env,
    string SuccessPattern,
    string? CountPattern,
    string? WorkingDirectory = null,
    string? TestSet = null,
    IReadOnlyList<string>? RemoteExcludes = null);

/// <summary>A test invocation ready to start.</summary>
/// <param name="Program">The runner to start.</param>
/// <param name="Arguments">Its arguments, one element each, never a shell string.</param>
/// <param name="Environment">The environment it runs with.</param>
/// <param name="WorkingDirectory">Where it starts, or null for the leg's tree root.</param>
public sealed record TestCommand(
    string Program,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? WorkingDirectory = null);
