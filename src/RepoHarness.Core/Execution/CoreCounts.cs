using System.Globalization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>How many cores a phase was given, and what decided it.</summary>
/// <param name="Value">The number of cores.</param>
/// <param name="Source">Where it came from, for the ledger: a leg reported as slow is read differently once this says why.</param>
public sealed record CoreCount(int Value, string Source);

/// <summary>
/// Resolves how many cores a build or a test run uses and hands the number to the runner explicitly.
/// </summary>
/// <remarks>
/// A runner left to its own default runs serially on one host and on every core on another, and legs
/// stop being comparable at all. Nor is "every core" the answer: a machine entirely claimed by a
/// build cannot be used for anything else while it runs.
/// </remarks>
public static class CoreCounts
{
    /// <summary>The name a <c>coresArgs</c> element spells inside braces where the number goes.</summary>
    /// <remarks>
    /// Named once and read by the other vocabulary rather than copied into it. Copied, the two drift
    /// apart the first time either is changed, and what drifting looks like is one of them refusing
    /// a string the other was about to fill in.
    /// </remarks>
    public const string PlaceholderName = "cores";

    /// <summary>The placeholder a <c>coresArgs</c> element carries where the number goes.</summary>
    public const string Placeholder = "{" + PlaceholderName + "}";

    /// <summary>
    /// The core count for a phase: the invocation's own <c>cores</c>, else the host's
    /// <c>buildCores</c> or <c>testCores</c>, else <c>defaults</c>, else the built-in
    /// <see cref="HarnessDefaults.DefaultCores"/>. A remote host rarely has the same core count as
    /// the machine that wrote the configuration, which is why the host's value outranks the default.
    /// </summary>
    /// <param name="invocation">The invocation's <c>cores</c>, when it declares one.</param>
    /// <param name="host">The host's <c>buildCores</c> or <c>testCores</c>, when it declares one.</param>
    /// <param name="defaults">The <c>defaults</c> value for this kind of phase, when there is one.</param>
    /// <exception cref="HarnessException">
    /// A declared count is not at least one. Refused rather than clamped: a runner handed zero cores
    /// either refuses or runs serially, and which of the two it is decides how long a gate takes.
    /// </exception>
    public static CoreCount Resolve(int? invocation, int? host, int? defaults)
    {
        if (invocation is { } fromInvocation)
        {
            return new CoreCount(Positive(fromInvocation, "the invocation's 'cores'"), "invocation");
        }

        if (host is { } fromHost)
        {
            return new CoreCount(Positive(fromHost, "the host's core count"), "host");
        }

        if (defaults is { } fromDefaults)
        {
            return new CoreCount(Positive(fromDefaults, "defaults"), "defaults");
        }

        return new CoreCount(HarnessDefaults.DefaultCores, "built-in default");
    }

    /// <summary>
    /// The arguments that hand <paramref name="cores"/> to a runner: <paramref name="coresArgs"/>
    /// with <see cref="Placeholder"/> replaced, or nothing at all when <paramref name="args"/>
    /// already sets that option. An explicit option in the invocation's own arguments wins, because
    /// spliced-in arguments would contradict it and which of the two the runner honours is its own
    /// business, not something the harness can state in a report.
    /// </summary>
    /// <param name="coresArgs">The configured <c>coresArgs</c>, if any.</param>
    /// <param name="cores">The resolved core count.</param>
    /// <param name="args">The invocation's own arguments.</param>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string>? coresArgs, int cores, IReadOnlyList<string>? args)
    {
        if (coresArgs is not { Count: > 0 })
        {
            return [];
        }

        if (args is { Count: > 0 } && Sets(args, coresArgs[0]))
        {
            return [];
        }

        var value = cores.ToString(CultureInfo.InvariantCulture);
        return [.. coresArgs.Select(argument => argument.Replace(Placeholder, value, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The environment that hands <paramref name="cores"/> to a runner, one variable per name in
    /// <paramref name="coresEnv"/>. A variable is preferred over an argument wherever the runner
    /// reads one: the runner itself then decides between it and an explicit option, which is the
    /// only place that decision can be made correctly.
    /// </summary>
    /// <param name="coresEnv">The configured <c>coresEnv</c> variable names, if any.</param>
    /// <param name="cores">The resolved core count.</param>
    public static IReadOnlyDictionary<string, string?> Environment(IReadOnlyList<string>? coresEnv, int cores)
    {
        var value = cores.ToString(CultureInfo.InvariantCulture);
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var name in coresEnv ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                variables[name.Trim()] = value;
            }
        }

        return variables;
    }

    /// <summary>
    /// Whether <paramref name="args"/> already sets <paramref name="option"/>, in any of the three
    /// spellings a tool accepts: <c>-j 8</c>, <c>--jobs=8</c> and the joined short form <c>-j8</c>.
    /// Recognising only the first would splice a second <c>-j</c> next to the one already there.
    /// </summary>
    private static bool Sets(IReadOnlyList<string> args, string option)
    {
        if (string.IsNullOrEmpty(option) || option[0] != '-')
        {
            // The first element is not an option but a value, so there is no option to look for and
            // nothing in args can be said to contradict it.
            return false;
        }

        var joinable = !option.StartsWith("--", StringComparison.Ordinal) && option.Length > 1;

        foreach (var argument in args)
        {
            if (string.Equals(argument, option, StringComparison.Ordinal)
                || argument.StartsWith(option + "=", StringComparison.Ordinal)
                || (joinable && argument.Length > option.Length && argument.StartsWith(option, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static int Positive(int cores, string source)
        => cores >= 1
            ? cores
            : throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{source} is {cores}; a core count must be at least 1.");
}
