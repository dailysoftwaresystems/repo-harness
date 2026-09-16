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
