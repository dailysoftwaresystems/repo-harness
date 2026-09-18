using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Testing;

namespace RepoHarness.Core.Legs;

/// <summary>
/// The programs a leg's build and test start, on whichever host runs the leg.
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

    /// <summary>The programs <paramref name="leg"/>'s build and test start.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    public static IReadOnlyList<string> For(HarnessConfig config, LegConfig leg)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);

        var programs = new List<string>();
        var project = VariantKey.ProjectFor(config, leg);
        var variant = VariantKey.For(config, leg, leg.Os);

        if (project is not null && variant.Buildable && BuildAdapters.Find(project.Type) is { } adapter)
        {
            programs.Add(adapter.Program);

            if (adapter is CMakeAdapter
                && config.Toolchains.TryGetValue(variant.Toolchain, out var toolchain)
                && toolchain.Generator?.StartsWith("Ninja", StringComparison.OrdinalIgnoreCase) == true)
            {
                programs.Add(NinjaDependencyCheck.Program);
            }

            var overlay = variant.Overlay(config, project);

            foreach (var variable in CompilerVariables)
            {
                if (overlay.Env.TryGetValue(variable, out var compiler) && ProgramIn(compiler) is { } program)
                {
                    programs.Add(program);
                }
            }
        }

        if (TestInvocationResolver.SettingsFor(config, leg, project) is { } settings
            && TestInvocationResolver.RunnerFor(settings, leg.Os) is { } runner)
        {
            programs.Add(runner);
        }

        return [.. programs.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every program any declared leg's build and test start, and every declared tool: what a host is
    /// asked to find.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <remarks>
    /// Wider than what any one leg needs, on purpose. Finding a program is a look at a file, so asking
    /// a host about all of them costs nothing a second round trip would not cost more, and a declared
    /// tool found off the PATH still has to reach the PATH of the leg that starts it from a step —
    /// tclsh in /opt/homebrew/bin, run by a corpus's own script.
    /// </remarks>
    public static IReadOnlyList<string> Wanted(HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return [.. config.Legs.Values
            .SelectMany(leg => For(config, leg))
            .Concat(config.Tools.Select(tool => tool.Name))
            .Where(program => !string.IsNullOrWhiteSpace(program))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The program a compiler variable starts: its first word, since a variable may carry options
    /// after it (<c>gcc -m32</c>).
    /// </summary>
    private static string? ProgramIn(string? value)
        => value?.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is [var first, ..]
            ? first
            : null;
}
