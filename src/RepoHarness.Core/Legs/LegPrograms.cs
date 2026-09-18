using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Testing;

namespace RepoHarness.Core.Legs;

/// <summary>
/// The programs a leg starts for a command, on whichever host runs the leg: those the host must have
/// installed, and those it is asked about.
/// </summary>
/// <remarks>
/// What a survey needs to know before it may call a leg runnable. Every program a repository
/// declares under <c>tools</c> is the wrong question: a tool declared once and needed only by one
/// runner, or only where its install entry says, would make every other leg on a host without it
/// unrunnable. And no question at all is how a survey answered "8 of 8 can run" about two legs whose
/// build tool their host could not find.
/// <para>
/// Derived from the configuration the build and the test themselves read, through the same names:
/// the adapter's own program, ninja when the toolchain asks for the Ninja generator, which cmake starts
/// to build, the compilers <c>CC</c> and <c>CXX</c> name - the variant's, or the host's where the
/// variant names none - and the test settings' runner.
/// A leg only ever runs on a
/// host whose operating system is its own, so all of it is decided from the leg's <c>os</c>, before
/// any host has been asked anything.
/// </para>
/// </remarks>
public static class LegPrograms
{
    /// <summary>The programs <paramref name="leg"/> starts for <paramref name="workload"/> that its host must have.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="workload">What the command has the leg do.</param>
    /// <param name="host">What the host the leg is placed on declares for itself.</param>
    /// <remarks>
    /// Not one started under an environment that sets PATH: that one is found on that PATH, which no
    /// survey can see, and demanded beforehand a compiler only that PATH holds would turn away a leg
    /// that builds. It is the run's to find - and still asked about, see <see cref="Wanted"/>. A host
    /// whose own environment sets PATH starts every one of them under it, so it is required to have
    /// none of them.
    /// </remarks>
    public static IReadOnlyList<string> For(HarnessConfig config, LegConfig leg, LegWorkload workload, HostSettings host)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(host);

        if (ProcessRunner.SetsPath(host.Env.Keys))
        {
            return [];
        }

        return [.. Starts(config, leg, workload, host.Env)
            .Where(start => !start.UnderOwnPath)
            .Select(start => start.Program)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every program any declared leg starts to build and test, what <paramref name="workload"/>
    /// starts besides, and every declared tool: what a host is asked to find.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="workload">What the command has each leg do.</param>
    /// <remarks>
    /// Wider than what any one leg needs, on purpose. Finding a program is a look at a file, so asking
    /// a host about all of them costs nothing a second round trip would not cost more, and a declared
    /// tool found off the PATH still has to reach the PATH of the leg that starts it from a step —
    /// tclsh in /opt/homebrew/bin, run by a corpus's own script. Built from what each leg starts, as
    /// <see cref="For"/> is, so a program a leg needs is never one its host was not asked about. One
    /// started under an environment that sets PATH is asked about too, though no leg is turned away
    /// for it: the directory the survey finds it in is appended to that PATH like any other. So is a
    /// compiler any host names in its own environment, wherever a leg might land, and the command a
    /// host's <c>keepAwake</c> starts - which no leg is turned away for either: a host it cannot hold
    /// awake still runs its legs, and a sleep there still marks their timings suspect.
    /// </remarks>
    public static IReadOnlyList<string> Wanted(HarnessConfig config, LegWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(workload);

        var everything = LegWorkload.BuildAndTest with { Programs = workload.Programs, UnderOwnPath = workload.UnderOwnPath };

        return [.. config.Legs.Values
            .SelectMany(leg => HostEnvironments(config).SelectMany(environment => Starts(config, leg, everything, environment)))
            .Select(start => start.Program)
            .Concat(config.Tools.Select(tool => tool.Name).Where(name => Surveyable(name, platformKey: null)))
            .Concat(DeclaredHosts(config)
                .Select(host => host.KeepAwake is [var program, ..] ? program : null)
                .OfType<string>()
                .Where(program => Surveyable(program, platformKey: null)))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>What every host declares under <c>env</c>, and the nothing a host with no section declares.</summary>
    private static IEnumerable<IReadOnlyDictionary<string, string>> HostEnvironments(HarnessConfig config)
        => [new Dictionary<string, string>(), .. DeclaredHosts(config).Select(host => host.Env)];

    /// <summary>Every host section the configuration declares.</summary>
    private static IEnumerable<HostSettings> DeclaredHosts(HarnessConfig config)
        => [config.Hosts.Local, .. config.Hosts.Wsl.Values, .. config.Hosts.Ssh.Values];

    /// <summary>
    /// Every program <paramref name="leg"/> starts for <paramref name="workload"/> that a survey can
    /// look for, on a host declaring <paramref name="hostEnvironment"/>, and whether it starts under
    /// an environment that sets PATH.
    /// </summary>
    private static IEnumerable<(string Program, bool UnderOwnPath)> Starts(
        HarnessConfig config,
        LegConfig leg,
        LegWorkload workload,
        IReadOnlyDictionary<string, string> hostEnvironment)
    {
        var project = VariantKey.ProjectFor(config, leg);
        var starts = new List<(string Program, bool UnderOwnPath)>();

        if (workload.Build)
        {
            starts.AddRange(BuildPrograms(config, leg, project, hostEnvironment));
        }

        if (workload.Test
            && TestInvocationResolver.SettingsFor(config, leg, project) is { } settings
            && TestInvocationResolver.InvocationFor(settings, leg.Os) is { Runner.Length: > 0 } invocation)
        {
            starts.Add((invocation.Runner, ProcessRunner.SetsPath(invocation.Env.Keys)));
        }

        starts.AddRange(workload.Programs.Select(program => (program, false)));
        starts.AddRange(workload.UnderOwnPath.Select(program => (program, true)));

        return starts.Where(start => Surveyable(start.Program, leg.Os));
    }

    /// <summary>
    /// What building <paramref name="leg"/> starts, when it builds anything, and whether under an
    /// environment that sets PATH.
    /// </summary>
    /// <remarks>
    /// All of it starts under the build's environment - cmake, the compilers and the ninja cmake
    /// starts - so where that environment sets PATH, all of it is looked for on that PATH, and where
    /// each is found is that environment's to say. That environment is the host's with the variant's
    /// over it, so a compiler the host names is the one the build starts wherever the variant names
    /// none.
    /// </remarks>
    private static IEnumerable<(string Program, bool UnderOwnPath)> BuildPrograms(
        HarnessConfig config,
        LegConfig leg,
        ProjectConfig? project,
        IReadOnlyDictionary<string, string> hostEnvironment)
    {
        var variant = VariantKey.For(config, leg, leg.Os);

        if (project is null || !variant.Buildable || BuildAdapters.Find(project.Type) is not { } adapter)
        {
            yield break;
        }

        var overlay = variant.Overlay(config, project);
        var ownPath = ProcessRunner.SetsPath(overlay.Env.Keys);

        yield return (adapter.Program, ownPath);

        if (adapter is CMakeAdapter
            && config.Toolchains.TryGetValue(variant.Toolchain, out var toolchain)
            && toolchain.Generator?.StartsWith("Ninja", StringComparison.OrdinalIgnoreCase) == true)
        {
            yield return (NinjaDependencyCheck.Program, ownPath);
        }

        var environment = PhaseEnvironment.Layered(hostEnvironment, overlay.Env);

        foreach (var variable in CompilerValue.Variables)
        {
            if (CompilerValue.For(variable, overlay.CacheVars, environment) is { } compiler)
            {
                yield return (compiler.Program, ownPath);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="program"/> is one a survey can look for on a
    /// <paramref name="platformKey"/> host: a name, or a path absolute there - absolute anywhere, for
    /// a declared tool, which is no one platform's - and nothing a placeholder completes.
    /// </summary>
    /// <remarks>
    /// A relative path is the run's to find: it is read from the directory the leg's phase starts in,
    /// which a host's copy may not hold until the sync the run begins with, or which the build has not
    /// made yet. A placeholder is filled in only once the leg is running. Looked for beforehand, each
    /// is a program a survey would call missing that the run then finds - and one the run does not
    /// find fails its leg, saying which it was. An absolute path is a file an installer put there,
    /// such as a compiler kept out of every PATH, and is looked for as surely as a name is.
    /// </remarks>
    private static bool Surveyable(string program, string? platformKey)
        => !string.IsNullOrWhiteSpace(program)
            && !program.Contains('{', StringComparison.Ordinal)
            && (!ProcessRunner.IsPath(program)
                || (platformKey is null ? PlatformPaths.IsAbsoluteOnAnyPlatform(program) : PlatformPaths.IsAbsoluteOn(program, platformKey)));
}
