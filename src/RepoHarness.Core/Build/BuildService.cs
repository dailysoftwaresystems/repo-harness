using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Build;

/// <summary>One leg's build, with everything it needs already decided.</summary>
/// <param name="Leg">The leg's name, as the ledger shows it.</param>
/// <param name="TreeRoot">The tree this leg builds, which every path derives from.</param>
/// <param name="Project">The project to build.</param>
/// <param name="Variant">What makes this build different from every other leg's.</param>
/// <param name="PlatformKey">The operating system the build runs on.</param>
/// <param name="Cores">How many cores the build may use.</param>
/// <param name="RunDirectory">Where this run's logs go.</param>
/// <param name="Time">Whether to report the profile timing.</param>
public sealed record BuildRequest(
    string Leg,
    string TreeRoot,
    ProjectConfig Project,
    VariantKey Variant,
    string PlatformKey,
    int Cores,
    string RunDirectory,
    bool Time = false);

/// <summary>What one leg's build did.</summary>
/// <param name="Verdict">The verdict, and the sentence the ledger shows for it.</param>
/// <param name="BuildDirectory">The directory it built in.</param>
/// <param name="Phases">Each phase that ran, in order.</param>
/// <param name="RebuiltFromClean">Why the variant was rebuilt from clean, or null when it was not.</param>
/// <param name="Dependencies">What the build's dependency records say, or null when they were not read.</param>
public sealed record BuildResult(
    ReachedVerdict Verdict,
    string BuildDirectory,
    IReadOnlyList<PhaseResult> Phases,
    string? RebuiltFromClean,
    NinjaDependencyReport? Dependencies);

/// <summary>Building one leg.</summary>
public interface IBuildService
{
    /// <summary>Configures and builds one leg, and checks what it produced.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="request">The leg's build.</param>
    /// <param name="cancellationToken">Stops the build.</param>
    Task<BuildResult> BuildAsync(
        HarnessConfig config,
        BuildRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IBuildService"/>
public sealed class BuildService(
    PhaseRunner phaseRunner,
    BuildDirectoryGuard buildDirectoryGuard,
    NinjaDependencyCheck dependencyCheck,
    IFileSystem fileSystem,
    IHarnessOutput output) : IBuildService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "build";

    private readonly PhaseRunner _phaseRunner = phaseRunner;
    private readonly BuildDirectoryGuard _buildDirectoryGuard = buildDirectoryGuard;
    private readonly NinjaDependencyCheck _dependencyCheck = dependencyCheck;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <inheritdoc/>
    public async Task<BuildResult> BuildAsync(
        HarnessConfig config,
        BuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        var adapter = BuildAdapters.For(request.Project.Type);
        var buildDirectory = request.Variant.DirectoryUnder(request.TreeRoot);
        var overlay = Overlay(config, request);

        // Before configuring, not after: a directory configured from another worktree watches that
        // tree's sources, which produced both a false refusal and a silent wrong answer.
        _buildDirectoryGuard.Check(
            buildDirectory,
            request.TreeRoot,
            overlay.Env.GetValueOrDefault("CC"),
            adapter.BuildTypeOf(config, request.Variant.Config));

        var rebuilt = DecideCleanRebuild(request, buildDirectory);

        if (rebuilt is not null)
        {
            _output.Info(CommandName, $"{request.Leg}: rebuilding from clean, because {rebuilt}");
            _fileSystem.DeleteDirectory(buildDirectory);
        }

        _fileSystem.CreateDirectory(buildDirectory);

        var phases = new List<PhaseResult>();

        foreach (var phase in adapter.Phases(config, request, buildDirectory, overlay))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _phaseRunner
                .RunAsync(
                    phase with
                    {
                        TimingPatterns = request.Time ? config.BuildTimingRegex : [],
                        ClockStepToleranceMilliseconds = config.Defaults.ClockStepToleranceMilliseconds,
                        StallSeconds = config.Defaults.StallSeconds,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            phases.Add(result);

            if (!result.Passed)
            {
                return new BuildResult(result.Verdict(), buildDirectory, phases, rebuilt, null);
            }
        }

        var missing = MissingOutputs(request, buildDirectory);

        if (missing.Count > 0)
        {
            // A build tool can exit 0 having produced nothing, and the tests then run against a
            // binary left over from an earlier build. Reported as unwitnessed rather than failed:
            // the build reported success, and what is missing is the evidence, not the exit code.
            return new BuildResult(
                ReachedVerdict.Of(
                    LegVerdict.Unwitnessed,
                    $"the build exited 0 and produced none of: {string.Join(", ", missing)}"),
                buildDirectory,
                phases,
                rebuilt,
                null);
        }

        var dependencies = await ReadDependenciesAsync(request, buildDirectory, cancellationToken).ConfigureAwait(false);

        if (dependencies is { IsClean: false })
        {
            // An object with no recorded header dependencies is never rebuilt when a header it
            // includes changes, so the next build links yesterday's object and reports success.
            return new BuildResult(
                ReachedVerdict.Of(
                    LegVerdict.Failed,
                    $"{dependencies.WithoutHeaders.Count} object(s) recorded no header dependencies, "
                    + $"including {string.Join(", ", dependencies.WithoutHeaders.Take(3))}"),
                buildDirectory,
                phases,
                rebuilt,
                dependencies);
        }

        RecordInputs(request, buildDirectory, phases);

        return new BuildResult(ReachedVerdict.Of(LegVerdict.Passed), buildDirectory, phases, rebuilt, dependencies);
    }

    /// <summary>
    /// The environment and cache variables this leg builds with: the toolchain, then the build
    /// configuration, then the sanitizer, then the project, each layered over the last.
    /// </summary>
    private static VariantOverlay Overlay(HarnessConfig config, BuildRequest request)
    {
        var merged = new VariantOverlay();

        foreach (var layer in Layers(config, request))
        {
            foreach (var (name, value) in layer.Env)
            {
                merged.Env[name] = value;
            }

            foreach (var (name, value) in layer.CacheVars)
            {
                merged.CacheVars[name] = value;
            }
        }

        return merged;
    }

    private static IEnumerable<VariantOverlay> Layers(HarnessConfig config, BuildRequest request)
    {
        if (config.Toolchains.TryGetValue(request.Variant.Toolchain, out var toolchain))
        {
            yield return toolchain;
        }

        if (config.BuildConfigs.TryGetValue(request.Variant.Config, out var buildConfig))
        {
            yield return buildConfig;
        }

        if (request.Variant.Sanitizer is { } sanitizer && config.Sanitizers.TryGetValue(sanitizer, out var overlay))
        {
            yield return overlay;
        }

        yield return request.Project;
    }

    /// <summary>
    /// Whether this variant must be rebuilt from clean, and why.
    /// </summary>
    /// <remarks>
    /// Ninja, Make and MSBuild decide what is stale by ordering timestamps, which a stepped clock
    /// defeats without a word: an object stamped during a forward step looks newer than a source
    /// edited just after it. Where the previous build spanned a clock step, or where a changed input
    /// is not newer than the newest output, the variant is rebuilt from clean and the ledger says
    /// why. A stale binary reported as a pass is the one price an incremental build must never pay.
    /// </remarks>
    private string? DecideCleanRebuild(BuildRequest request, string buildDirectory)
    {
        var record = Path.Combine(buildDirectory, BuildRecordFileName);

        if (!_fileSystem.FileExists(record))
        {
            return null;
        }

        var previous = _fileSystem.ReadAllText(record);

        if (previous.Contains(ClockStepMarker, StringComparison.Ordinal))
        {
            return "the previous build spanned a clock step, so nothing it stamped can be ordered";
        }

        var newestOutput = NewestOutput(request, buildDirectory);

        if (newestOutput is null)
        {
            return null;
        }

        var stale = ChangedInputsNotNewerThan(request.TreeRoot, newestOutput.Value);

        return stale is null
            ? null
            : $"'{stale}' changed but is not newer than the newest output, which a stepped clock does";
    }

    private DateTime? NewestOutput(BuildRequest request, string buildDirectory)
    {
        DateTime? newest = null;

        foreach (var output in request.Project.BuildOutputs)
        {
            var path = Path.Combine(buildDirectory, output);

            if (!_fileSystem.FileExists(path))
            {
                continue;
            }

            var written = File.GetLastWriteTimeUtc(path);
            newest = newest is null || written > newest ? written : newest;
        }

        return newest;
    }

    /// <summary>
    /// The first tracked source that changed without becoming newer than the build's newest output.
    /// Compared by equality of content elsewhere; here the question is only whether the build system
    /// would notice, and the build system asks about times.
    /// </summary>
    private string? ChangedInputsNotNewerThan(string treeRoot, DateTime newestOutput)
    {
        foreach (var file in _fileSystem.EnumerateFiles(treeRoot, recursive: false))
        {
            var written = File.GetLastWriteTimeUtc(file);

            if (written > newestOutput)
            {
                continue;
            }

            if (written.AddSeconds(ClockStepSuspicionSeconds) > newestOutput
                && written < newestOutput)
            {
                return Path.GetFileName(file);
            }
        }

        return null;
    }

    private void RecordInputs(BuildRequest request, string buildDirectory, IReadOnlyList<PhaseResult> phases)
    {
        var stepped = phases.Any(phase => phase.ClockStepped);

        _fileSystem.WriteAllTextAtomic(
            Path.Combine(buildDirectory, BuildRecordFileName),
            (stepped ? ClockStepMarker : "clean") + "\n" + request.Variant.DirectoryName + "\n");
    }

    private List<string> MissingOutputs(BuildRequest request, string buildDirectory)
        => [.. request.Project.BuildOutputs
            .Where(output => !_fileSystem.FileExists(Path.Combine(buildDirectory, output)))];

    private async Task<NinjaDependencyReport?> ReadDependenciesAsync(
        BuildRequest request,
        string buildDirectory,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Project.Type, "cmake", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await _dependencyCheck.CheckAsync(buildDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            // Reported, never fatal on its own: the build itself succeeded, and a dependency record
            // that could not be read is a fact about the check rather than about the code.
            _output.Warn(CommandName, $"{request.Leg}: dependency records could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>What the harness records beside a build, so the next one can tell what happened.</summary>
    private const string BuildRecordFileName = ".harness-build";

    /// <summary>What that record says when the build it describes spanned a clock step.</summary>
    private const string ClockStepMarker = "clock-stepped";

    /// <summary>
    /// How far behind the newest output a changed input may be before it is treated as a clock step
    /// rather than an ordinary older file. The measured host steps its clock about 25 seconds.
    /// </summary>
    private const int ClockStepSuspicionSeconds = 60;
}
