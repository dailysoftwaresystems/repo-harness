namespace RepoHarness.Core.Configuration;

/// <summary>Environment and cache variables layered onto a build.</summary>
public class VariantOverlay
{
    /// <summary>
    /// Environment variables set for every phase of the build. Names compare ignoring
    /// case on every platform, as Windows compares them, so a file declaring both
    /// <c>PATH</c> and <c>Path</c> is rejected when read rather than resolved one way on
    /// Windows and another elsewhere.
    /// </summary>
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Build system cache variables, such as CMake <c>-D</c> definitions. Names compare
    /// ignoring case like every other name in the file, so two entries differing only in
    /// case are rejected rather than both passed through.
    /// </summary>
    public Dictionary<string, string> CacheVars { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A compiler selection.</summary>
public sealed class ToolchainConfig : VariantOverlay
{
    /// <summary>
    /// Platforms this toolchain exists on (<c>windows</c>, <c>linux</c>, <c>macos</c>) or
    /// <c>all</c>. A leg naming a toolchain absent from its own operating system is refused when
    /// the file is read, rather than attempted and failed later as a missing compiler.
    /// </summary>
    /// <remarks>
    /// Refused when read rather than skipped when placed, because nothing about it needs measuring:
    /// a leg's <c>os</c> is required, and a leg only ever runs on a host whose operating system
    /// equals it — emulation varies the processor, never the system — so the contradiction is
    /// between two lines of this file and nothing else. Skipping instead would also make a run
    /// report legs that reached no verdict, which is not a success, so a wrong list here would turn
    /// a green run non-zero rather than telling its author to fix the line. <c>tools</c> carries a
    /// list of the same shape, and both are read through <see cref="Platform.PlatformScope"/>.
    /// </remarks>
    public List<string> Platforms { get; init; } = ["all"];

    /// <summary>Build system generator to request, such as <c>Ninja</c>.</summary>
    public string? Generator { get; init; }
}

/// <summary>A named build configuration.</summary>
public sealed class BuildConfiguration : VariantOverlay
{
    /// <summary>CMake build type, for cmake projects.</summary>
    public string? CmakeBuildType { get; init; }

    /// <summary>MSBuild configuration name, for dotnet projects.</summary>
    public string? DotnetConfiguration { get; init; }

    /// <summary>Build mode, for dart and flutter projects.</summary>
    public string? DartMode { get; init; }
}

/// <summary>A buildable project.</summary>
public sealed class ProjectConfig : VariantOverlay
{
    /// <summary>Name used to select this project on the command line.</summary>
    public required string Name { get; init; }

    /// <summary>Adapter that builds it: <c>cmake</c>, <c>dotnet</c> or <c>dart</c>.</summary>
    public required string Type { get; init; }

    /// <summary>
    /// Path to the project relative to the tree root: a directory for cmake and dart,
    /// the solution or project file itself for dotnet.
    /// </summary>
    public string Path { get; init; } = ".";

    /// <summary>Targets to build. Empty builds the project's own default target.</summary>
    public List<string> Targets { get; init; } = [];

    /// <summary>
    /// Files, relative to the build directory, that must exist after a build for it to count
    /// as passed. A build tool can exit 0 having produced nothing, and handing the tests a
    /// stale binary from an earlier build is exactly the result that looks like success.
    /// </summary>
    /// <remarks>
    /// An entry is a path, or a mapping of platform to path where the platforms disagree about what
    /// the same target is called — a program is <c>dsscp</c> on one and <c>dsscp.exe</c> on another.
    /// A bare path applies everywhere, so a list written before this still means what it did.
    /// </remarks>
    public List<BuildOutput> BuildOutputs { get; init; } = [];

    /// <summary>Default toolchain per platform, used when a leg names none.</summary>
    public Dictionary<string, string> DefaultToolchain { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How to run this project's tests, unless a leg overrides it.</summary>
    public TestConfig? Test { get; init; }
}
