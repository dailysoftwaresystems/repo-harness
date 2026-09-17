using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Build;

/// <summary>
/// How one kind of project is configured and built. One adapter per build system, chosen by the
/// project's declared type, so nothing about a build system is compiled into the leg machinery.
/// </summary>
public interface IBuildAdapter
{
    /// <summary>The project type this adapter serves.</summary>
    string Type { get; }

    /// <summary>
    /// The kinds of file whose content can change what this project type builds: extensions and
    /// whole file names, in one list.
    /// </summary>
    /// <remarks>
    /// What a clean rebuild is decided over. Every tracked file was the first answer and it is the
    /// safe one, but it makes a documentation edit discard a warm build directory on every leg:
    /// measured on a consumer's tree, editing one markdown file forced two legs through a full
    /// rebuild, one of them 11 minutes.
    /// <para>
    /// Narrowing is safe here for a reason that is worth stating, because the check exists to stop
    /// a stale binary reading as a pass. A build that spanned a clock step is rebuilt from clean
    /// before this set is ever consulted, so the host whose clock moves is unaffected. What is left
    /// is builds where no step occurred, and there the build system's own dependency graph is
    /// authoritative: ninja will rebuild what a changed file feeds, because the timestamps it
    /// compares mean what they say. This set is the belt over those braces, not the braces.
    /// </para>
    /// <para>
    /// Empty means every tracked file, which is what an adapter that cannot say says.
    /// </para>
    /// </remarks>
    BuildInputKinds InputKinds { get; }

    /// <summary>The build type this configuration names for this build system, or null when it names none.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="buildConfig">The build configuration's name.</param>
    string? BuildTypeOf(HarnessConfig config, string buildConfig);

    /// <summary>The phases that configure and build this project, in order.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="request">The leg's build.</param>
    /// <param name="buildDirectory">Where this variant builds.</param>
    /// <param name="overlay">The environment and cache variables this variant builds with.</param>
    IEnumerable<PhaseRequest> Phases(
        HarnessConfig config,
        BuildRequest request,
        string buildDirectory,
        VariantOverlay overlay);
}

/// <summary>
/// The kinds of file whose content can change what a build produces.
/// </summary>
/// <param name="Entries">
/// Extensions and whole file names in one list, each as configuration spells it: <c>.cpp</c>,
/// <c>cpp</c> and <c>CMakeLists.txt</c> all mean what they look like.
/// </param>
/// <remarks>
/// Kinds rather than globs: the question is what kind of file this is, and a glob would have to be
/// written per directory layout by every repository that has one. One list rather than extensions
/// beside names, because a project overriding this writes the same shape the adapters do, and two
/// lists would mean a project that declared only formats silently lost its build system's own files.
/// </remarks>
public sealed record BuildInputKinds(IReadOnlyList<string> Entries)
{
    /// <summary>An adapter that cannot say, whose builds compare over every tracked file.</summary>
    public static BuildInputKinds Everything { get; } = new([]);

    /// <summary>Whether this says anything at all.</summary>
    public bool Narrows => Entries.Count > 0;

    /// <summary>Whether <paramref name="relativePath"/> is a file of one of these kinds.</summary>
    /// <param name="relativePath">A path relative to the tree root, spelled with either separator.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    /// <remarks>
    /// A file with no extension at all is covered, whatever the entries say. An extension is the
    /// signal this reads, and a file carrying none carries no signal — so the safe reading is that
    /// it might matter. Measured on a consumer's tree: <c>VERSION</c>, extensionless, is read with
    /// <c>file(READ)</c> at configure time and feeds both the project version and a compiler
    /// predefine. Listing that name, and the next repository's, would be guessing at a vocabulary;
    /// treating "no extension" as unknown costs a clean rebuild when somebody edits <c>LICENSE</c>,
    /// which is rare and is the direction to be wrong in.
    /// <para>
    /// An entry matches a file's whole name or its extension, with or without the leading dot, so
    /// one list carries both questions and nobody has to know which kind of entry they wrote.
    /// </para>
    /// </remarks>
    public bool Covers(string relativePath, StringComparison comparison)
    {
        if (!Narrows)
        {
            return true;
        }

        var name = Path.GetFileName(relativePath.Replace('\\', '/'));
        var extension = Path.GetExtension(name);

        if (extension.Length == 0)
        {
            return true;
        }

        foreach (var entry in Entries)
        {
            if (name.Equals(entry, comparison))
            {
                return true;
            }

            // The extension compared without case wherever it is compared: a tracked file named
            // .CPP builds the same as one named .cpp everywhere, and a case-sensitive filesystem
            // does not make it a different kind of file.
            var spelled = entry.StartsWith('.') ? entry : "." + entry;

            if (extension.Equals(spelled, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The adapters this build knows, by project type.</summary>
public static class BuildAdapters
{
    private static readonly IBuildAdapter[] Known = [new CMakeAdapter(), new DotnetAdapter(), new DartAdapter()];

    /// <summary>The adapter for a project type.</summary>
    /// <param name="type">The project's declared type.</param>
    /// <exception cref="HarnessException">No adapter serves that type.</exception>
    public static IBuildAdapter For(string type)
    {
        var adapter = Known.FirstOrDefault(
            candidate => string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));

        return adapter ?? throw new HarnessException(
            HarnessExit.ConfigInvalid,
            $"No build adapter serves project type '{type}'. Declared types: "
            + string.Join(", ", Known.Select(candidate => candidate.Type)) + ".");
    }

    /// <summary>
    /// The environment a phase runs with: the variant's own, plus the compiler cache directories a
    /// host declares, which are set explicitly so two hosts never share one store.
    /// </summary>
    /// <param name="overlay">The variant's environment.</param>
    /// <param name="treeRoot">The leg's tree, which the cache is keyed against.</param>
    internal static Dictionary<string, string?> EnvironmentFor(VariantOverlay overlay, string treeRoot)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in overlay.Env)
        {
            environment[name] = value;
        }

        // Set from the leg's own tree rather than inherited. A shared base directory lets one tree's
        // objects satisfy another tree's build, which is a contamination this prevents by
        // construction rather than by hoping the caches disagree.
        if (environment.ContainsKey("CCACHE_DIR"))
        {
            environment["CCACHE_BASEDIR"] = treeRoot;
        }

        return environment;
    }

    internal static string LogFor(BuildRequest request, string phase)
        => Path.Combine(request.RunDirectory, request.Leg, phase + ".log");
}

/// <summary>Configures and builds a CMake project.</summary>
public sealed class CMakeAdapter : IBuildAdapter
{
    /// <inheritdoc/>
    public string Type => "cmake";

    /// <inheritdoc/>
    /// <remarks>
    /// Sources and headers, the build system's own files, and the templates <c>configure_file</c>
    /// reads. Assembly and the CUDA and Objective-C families are here because a project that has
    /// them compiles them; listing only C and C++ would leave a repository that builds a <c>.S</c>
    /// deciding a changed one cannot affect its build.
    /// </remarks>
    public BuildInputKinds InputKinds { get; } = new(
        [
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".m", ".mm", ".cu", ".cuh",
            ".h", ".hh", ".hpp", ".hxx", ".h++", ".inc", ".ipp", ".tcc", ".def",
            ".s", ".asm", ".rc", ".manifest",
            ".cmake", ".in", ".ninja", ".make", ".mk", ".pc",
            "CMakeLists.txt", "Makefile", "makefile", "GNUmakefile", "meson.build", "conanfile.txt", "conanfile.py", "vcpkg.json",
        ]);

    /// <inheritdoc/>
    public string? BuildTypeOf(HarnessConfig config, string buildConfig)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.BuildConfigs.TryGetValue(buildConfig, out var declared) ? declared.CmakeBuildType : null;
    }

    /// <inheritdoc/>
    public IEnumerable<PhaseRequest> Phases(
        HarnessConfig config,
        BuildRequest request,
        string buildDirectory,
        VariantOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(overlay);

        var environment = BuildAdapters.EnvironmentFor(overlay, request.TreeRoot);
        var source = Path.Combine(request.TreeRoot, request.Project.Path);
        var configure = new List<string> { "-S", source, "-B", buildDirectory };

        if (config.Toolchains.TryGetValue(request.Variant.Toolchain, out var toolchain)
            && toolchain.Generator is { Length: > 0 } generator)
        {
            configure.Add("-G");
            configure.Add(generator);
        }

        if (BuildTypeOf(config, request.Variant.Config) is { Length: > 0 } buildType)
        {
            configure.Add($"-DCMAKE_BUILD_TYPE={buildType}");
        }

        foreach (var (name, value) in overlay.CacheVars)
        {
            configure.Add($"-D{name}={value}");
        }

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = "configure",
            FileName = "cmake",
            Arguments = configure,
            WorkingDirectory = request.TreeRoot,
            Environment = environment,
            LogFile = BuildAdapters.LogFor(request, "configure"),
        };

        var build = new List<string>
        {
            "--build", buildDirectory,
            "--parallel", request.Cores.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        foreach (var target in request.Project.Targets)
        {
            build.Add("--target");
            build.Add(target);
        }

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = "build",
            FileName = "cmake",
            Arguments = build,
            WorkingDirectory = request.TreeRoot,
            Environment = environment,
            LogFile = BuildAdapters.LogFor(request, "build"),
        };
    }
}

/// <summary>Builds a .NET project or solution.</summary>
public sealed class DotnetAdapter : IBuildAdapter
{
    /// <inheritdoc/>
    public string Type => "dotnet";

    /// <inheritdoc/>
    /// <remarks>
    /// Sources, project and solution files, and the files MSBuild imports on its own — a
    /// <c>Directory.Build.props</c> changes every assembly under it without appearing in any of
    /// them. Resources and settings are here because they are compiled in.
    /// </remarks>
    public BuildInputKinds InputKinds { get; } = new(
        [
            ".cs", ".vb", ".fs", ".fsi", ".fsx", ".razor", ".cshtml", ".xaml",
            ".csproj", ".vbproj", ".fsproj", ".shproj", ".projitems", ".sln", ".slnx", ".slnf",
            ".props", ".targets", ".resx", ".settings", ".ruleset", ".editorconfig", ".json",
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "nuget.config", "NuGet.config", "global.json",
        ]);

    /// <inheritdoc/>
    public string? BuildTypeOf(HarnessConfig config, string buildConfig)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.BuildConfigs.TryGetValue(buildConfig, out var declared) ? declared.DotnetConfiguration : null;
    }

    /// <inheritdoc/>
    public IEnumerable<PhaseRequest> Phases(
        HarnessConfig config,
        BuildRequest request,
        string buildDirectory,
        VariantOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(overlay);

        var arguments = new List<string>
        {
            "build",
            Path.Combine(request.TreeRoot, request.Project.Path),

            // Each variant keeps its own output, so two legs never overwrite one another's
            // assemblies while both are being tested.
            $"--artifacts-path:{buildDirectory}",
        };

        if (BuildTypeOf(config, request.Variant.Config) is { Length: > 0 } configuration)
        {
            arguments.Add("--configuration");
            arguments.Add(configuration);
        }

        foreach (var (name, value) in overlay.CacheVars)
        {
            arguments.Add($"-p:{name}={value}");
        }

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = "build",
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = request.TreeRoot,
            Environment = BuildAdapters.EnvironmentFor(overlay, request.TreeRoot),
            LogFile = BuildAdapters.LogFor(request, "build"),
        };
    }
}

/// <summary>Builds a Dart or Flutter project.</summary>
public sealed class DartAdapter : IBuildAdapter
{
    /// <inheritdoc/>
    public string Type => "dart";

    /// <inheritdoc/>
    /// <remarks>
    /// Sources, the manifest and its lock, and the generated-code inputs a build reads. The lock is
    /// included because a resolved dependency set decides what is compiled as much as a source does.
    /// </remarks>
    public BuildInputKinds InputKinds { get; } = new(
        [".dart", ".yaml", ".yml", ".arb", ".json", "pubspec.lock"]);

    /// <inheritdoc/>
    public string? BuildTypeOf(HarnessConfig config, string buildConfig)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.BuildConfigs.TryGetValue(buildConfig, out var declared) ? declared.DartMode : null;
    }

    /// <inheritdoc/>
    public IEnumerable<PhaseRequest> Phases(
        HarnessConfig config,
        BuildRequest request,
        string buildDirectory,
        VariantOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(overlay);

        var project = Path.Combine(request.TreeRoot, request.Project.Path);

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = "pub-get",
            FileName = "dart",
            Arguments = ["pub", "get"],
            WorkingDirectory = project,
            Environment = BuildAdapters.EnvironmentFor(overlay, request.TreeRoot),
            LogFile = BuildAdapters.LogFor(request, "pub-get"),
        };

        var arguments = new List<string> { "compile", "exe" };

        foreach (var target in request.Project.Targets)
        {
            arguments.Add(target);
        }

        arguments.Add("--output");
        arguments.Add(Path.Combine(buildDirectory, "out"));

        if (BuildTypeOf(config, request.Variant.Config) is { Length: > 0 } mode)
        {
            arguments.Add($"--define=mode={mode}");
        }

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = "build",
            FileName = "dart",
            Arguments = arguments,
            WorkingDirectory = project,
            Environment = BuildAdapters.EnvironmentFor(overlay, request.TreeRoot),
            LogFile = BuildAdapters.LogFor(request, "build"),
        };
    }
}
