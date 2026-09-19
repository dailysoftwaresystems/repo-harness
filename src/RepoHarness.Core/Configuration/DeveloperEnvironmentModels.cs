namespace RepoHarness.Core.Configuration;

/// <summary>The kinds of developer environment this build knows how to set up.</summary>
public static class DeveloperEnvironmentKinds
{
    /// <summary>
    /// A Visual Studio instance's own environment: the one <c>vcvarsall.bat</c> sets for the leg's
    /// processor, holding <c>cl</c>, the linker, the Windows SDK and their <c>INCLUDE</c> and
    /// <c>LIB</c>.
    /// </summary>
    public const string VisualStudio = "visualStudio";

    /// <summary>Every kind, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [VisualStudio];
}

/// <summary>
/// An environment a toolchain's builds need set up on the host that runs them, before anything is
/// started there - declared once, and named by each toolchain that needs it.
/// </summary>
/// <remarks>
/// Set up by the harness on the machine doing the work, never written into the configuration. A
/// machine's own <c>INCLUDE</c>, <c>LIB</c> and <c>PATH</c> committed under a host's <c>env</c> would
/// hold every machine to one machine's Visual Studio and SDK, and break on the next update of either.
/// </remarks>
public sealed class DeveloperEnvironmentConfig
{
    /// <summary>The component Visual Studio's own installer lists for the C++ build tools on x86 and x64.</summary>
    public const string DefaultVisualStudioComponent = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";

    /// <summary>What sets it up: one of <see cref="DeveloperEnvironmentKinds.All"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The component an instance must have to be chosen, as Visual Studio's installer names it; the
    /// C++ build tools by default. The newest instance that has it is the one set up.
    /// </summary>
    public string RequiresComponent { get; init; } = DefaultVisualStudioComponent;
}
