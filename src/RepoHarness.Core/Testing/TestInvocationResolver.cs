using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Testing;

/// <summary>
/// Works out how one leg runs its tests, from the project's settings, the leg's own, and the
/// platform it runs on.
/// </summary>
/// <remarks>
/// Three layers, each narrower than the last, because a leg reached through a transport legitimately
/// runs a narrower suite than one running here: a guard that checks this checkout has nothing to say
/// about a host's copy of it. Resolving them in one place is what keeps the runner, the success
/// pattern and the exclusions from being decided by three different rules.
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

        var platform = platformKey switch
        {
            PlatformNames.Windows => settings.Windows,
            PlatformNames.Linux => settings.Linux,
            PlatformNames.MacOs => settings.Macos,
            _ => null,
        };

        var merged = Merge(settings.All, platform);

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
    /// The command line for a resolved invocation, with the caller's filter and exclusions spliced
    /// in, and the core count handed over explicitly.
    /// </summary>
    /// <param name="invocation">The resolved invocation.</param>
    /// <param name="cores">The core count this leg runs with.</param>
    /// <param name="filter">A filter the caller asked for, or null.</param>
    /// <param name="excludes">Exclusions the caller asked for.</param>
    /// <remarks>
    /// The core count goes through <c>coresEnv</c> where the runner reads a variable and
    /// <c>coresArgs</c> where it does not. A variable is preferred because an explicit option in the
    /// invocation's own arguments still wins, decided by the runner itself, where a spliced-in
    /// option would contradict it and the runner would pick one without saying which.
    /// </remarks>
    public static TestCommand CommandFor(
        ResolvedTestInvocation invocation,
        int cores,
        string? filter,
        IReadOnlyList<string>? excludes)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var arguments = new List<string>(invocation.Args);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            if (string.IsNullOrWhiteSpace(invocation.FilterArg))
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    "A filter was given, but the test settings declare no filterArg, so nothing knows "
                    + "how to pass one to this runner.");
            }

            arguments.Add(invocation.FilterArg);
            arguments.Add(filter);
        }

        foreach (var exclude in excludes ?? [])
        {
            if (string.IsNullOrWhiteSpace(invocation.ExcludeArg))
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    "An exclusion was given, but the test settings declare no excludeArg, so nothing "
                    + "knows how to pass one to this runner.");
            }

            arguments.Add(invocation.ExcludeArg);
            arguments.Add(exclude);
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

        return new TestCommand(invocation.Runner, arguments, environment);
    }

    private static ResolvedTestInvocation Merge(TestInvocation? all, TestInvocation? platform)
        => new(
            platform?.Runner ?? all?.Runner ?? string.Empty,
            platform?.Args ?? all?.Args ?? [],
            platform?.FilterArg ?? all?.FilterArg,
            platform?.ExcludeArg ?? all?.ExcludeArg,
            platform?.Cores ?? all?.Cores,
            platform?.CoresArgs ?? all?.CoresArgs ?? [],
            platform?.CoresEnv ?? all?.CoresEnv ?? [],
            platform?.Env ?? all?.Env ?? [],
            platform?.SuccessPattern ?? all?.SuccessPattern ?? string.Empty,
            platform?.CountPattern ?? all?.CountPattern);
}

/// <summary>One test invocation, with every field decided.</summary>
/// <param name="Runner">The test runner to start.</param>
/// <param name="Args">Its arguments.</param>
/// <param name="FilterArg">The argument that introduces a filter, or null.</param>
/// <param name="ExcludeArg">The argument that introduces an exclusion, or null.</param>
/// <param name="Cores">The core count this invocation asks for, or null to take the host's.</param>
/// <param name="CoresArgs">Arguments carrying the core count, with <c>{cores}</c> to replace.</param>
/// <param name="CoresEnv">Variables to set to the core count.</param>
/// <param name="Env">Environment for the invocation.</param>
/// <param name="SuccessPattern">What must appear in the runner's own output for this to pass.</param>
/// <param name="CountPattern">What captures how many tests ran, or null.</param>
public sealed record ResolvedTestInvocation(
    string Runner,
    IReadOnlyList<string> Args,
    string? FilterArg,
    string? ExcludeArg,
    int? Cores,
    IReadOnlyList<string> CoresArgs,
    IReadOnlyList<string> CoresEnv,
    IReadOnlyDictionary<string, string> Env,
    string SuccessPattern,
    string? CountPattern);

/// <summary>A test invocation ready to start.</summary>
/// <param name="Program">The runner to start.</param>
/// <param name="Arguments">Its arguments, one element each, never a shell string.</param>
/// <param name="Environment">The environment it runs with.</param>
public sealed record TestCommand(
    string Program,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);
