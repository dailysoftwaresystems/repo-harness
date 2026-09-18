using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Testing;

namespace RepoHarness.Core.Legs;

/// <summary>
/// The programs a leg starts for a command, on whichever host runs the leg, that the host must have
/// installed.
/// </summary>
/// <remarks>
/// What a survey needs to know before it may call a leg runnable. Every program a repository
/// declares under <c>tools</c> is the wrong question: a tool declared once and needed only by one
/// runner, or only where its install entry says, would make every other leg on a host without it
/// unrunnable. And no question at all is how a survey answered "8 of 8 can run" about two legs whose
/// build tool their host could not find.
/// <para>
/// Derived from the configuration the build and the test themselves read, through the same names:
/// the adapter's own program, ninja when the toolchain asks for the Ninja generator — cmake starts it
/// to build, and the build starts it again to read its dependency records — the compilers the
/// variant's <c>CC</c> and <c>CXX</c> name, and the test settings' runner. A leg only ever runs on a
/// host whose operating system is its own, so all of it is decided from the leg's <c>os</c>, before
/// any host has been asked anything.
/// </para>
/// </remarks>
public static class LegPrograms
{
    /// <summary>The variables a variant names its compilers in.</summary>
    private static readonly string[] CompilerVariables = ["CC", "CXX"];

    /// <summary>The programs <paramref name="leg"/> starts for <paramref name="workload"/> that its host must have.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="workload">What the command has the leg do.</param>
    public static IReadOnlyList<string> For(HarnessConfig config, LegConfig leg, LegWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(workload);

        var programs = new List<string>();
        var project = VariantKey.ProjectFor(config, leg);

        if (workload.Build)
        {
            programs.AddRange(BuildPrograms(config, leg, project));
        }

        if (workload.Test
            && TestInvocationResolver.SettingsFor(config, leg, project) is { } settings
            && TestInvocationResolver.RunnerFor(settings, leg.Os) is { } runner)
        {
            programs.Add(runner);
        }

        programs.AddRange(workload.Programs);

        return [.. programs.Where(Installed).Distinct(StringComparer.Ordinal)];
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
    /// tclsh in /opt/homebrew/bin, run by a corpus's own script. Built from <see cref="For"/> itself,
    /// so a program a leg needs is never one its host was not asked about.
    /// </remarks>
    public static IReadOnlyList<string> Wanted(HarnessConfig config, LegWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(workload);

        var everything = LegWorkload.BuildAndTest with { Programs = workload.Programs };

        return [.. config.Legs.Values
            .SelectMany(leg => For(config, leg, everything))
            .Concat(config.Tools.Select(tool => tool.Name).Where(Installed))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>What building <paramref name="leg"/> starts, when it builds anything.</summary>
    private static IEnumerable<string> BuildPrograms(HarnessConfig config, LegConfig leg, ProjectConfig? project)
    {
        var variant = VariantKey.For(config, leg, leg.Os);

        if (project is null || !variant.Buildable || BuildAdapters.Find(project.Type) is not { } adapter)
        {
            yield break;
        }

        yield return adapter.Program;

        if (adapter is CMakeAdapter
            && config.Toolchains.TryGetValue(variant.Toolchain, out var toolchain)
            && toolchain.Generator?.StartsWith("Ninja", StringComparison.OrdinalIgnoreCase) == true)
        {
            yield return NinjaDependencyCheck.Program;
        }

        var overlay = variant.Overlay(config, project);

        foreach (var variable in CompilerVariables)
        {
            if (overlay.Env.TryGetValue(variable, out var compiler) && FirstWord(compiler) is { } program)
            {
                yield return program;
            }
        }
    }

    /// <summary>
    /// The first word of a compiler variable, which is the program it starts when it names one by
    /// name: a variable may carry options after it (<c>gcc -m32</c>) or a launcher before the
    /// compiler (<c>ccache gcc</c>).
    /// </summary>
    /// <remarks>
    /// A value naming a path - <c>C:\Program Files\LLVM\bin\clang-cl.exe</c>, which CMake reads whole
    /// because the whole of it names a file - has a first word that is a path too, so it is passed over
    /// with every other path rather than cut at its first space and reported missing.
    /// </remarks>
    private static string? FirstWord(string? value)
        => value?.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
            ? first
            : null;

    /// <summary>
    /// Whether <paramref name="program"/> is one a host has installed, which a survey can look for: a
    /// name, rather than a path or something a placeholder completes.
    /// </summary>
    /// <remarks>
    /// Anything else is the run's to find. A relative path is written from the leg's own tree, which a
    /// host's copy may not hold until the sync the run begins with; a path under the build directory
    /// is a file the build makes; a placeholder is filled in only once the leg is running. Looked for
    /// beforehand, each is a program a survey would call missing that the run then finds - and one the
    /// run does not find fails its leg, saying which it was.
    /// </remarks>
    private static bool Installed(string program)
        => !string.IsNullOrWhiteSpace(program) && !ProcessRunner.IsPath(program) && !program.Contains('{', StringComparison.Ordinal);
}
