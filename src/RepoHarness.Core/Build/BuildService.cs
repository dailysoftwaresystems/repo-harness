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
    InputFingerprint fingerprints,
    Git.IGitClient gitClient,
    IFileSystem fileSystem,
    IHarnessOutput output) : IBuildService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "build";

    private readonly PhaseRunner _phaseRunner = phaseRunner;
    private readonly BuildDirectoryGuard _buildDirectoryGuard = buildDirectoryGuard;
    private readonly NinjaDependencyCheck _dependencyCheck = dependencyCheck;
    private readonly InputFingerprint _fingerprints = fingerprints;
    private readonly Git.IGitClient _gitClient = gitClient;
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
        // tree's sources, which produced both a false refusal and a silent wrong answer. Compared
        // against the directory the build is actually configured from, which is the project's own
        // path below the tree root and not the tree root itself.
        _buildDirectoryGuard.Check(
            buildDirectory,
            Path.Combine(request.TreeRoot, request.Project.Path),
            overlay.Env.GetValueOrDefault("CC"),
            overlay.Env.GetValueOrDefault("CXX"),
            adapter.BuildTypeOf(config, request.Variant.Config));

        var rebuilt = await DecideCleanRebuildAsync(request, buildDirectory, cancellationToken).ConfigureAwait(false);

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

        if (request.Project.BuildOutputs.Count == 0)
        {
            // Nothing declared is nothing witnessed. A build tool exits 0 having produced nothing
            // often enough — a target filtered out, a generator writing somewhere else — that the
            // exit code alone is not evidence, and an empty list makes the check below vacuously
            // true rather than absent, which reads as a pass nobody performed.
            return new BuildResult(
                ReachedVerdict.Of(
                    LegVerdict.Unwitnessed,
                    $"project '{request.Project.Name}' declares no buildOutputs, so nothing established that "
                    + "this build produced anything; a zero exit code is not that evidence"),
                buildDirectory,
                phases,
                rebuilt,
                null);
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

        var (dependencies, unreadable) = await ReadDependenciesAsync(request, buildDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (unreadable is not null)
        {
            // Not a pass. The check exists because an object with no recorded headers is never
            // rebuilt when a header changes, and a check that could not run has established nothing
            // about that — which is what unmeasured means, and is a different fact from "clean".
            return new BuildResult(
                ReachedVerdict.Of(
                    LegVerdict.Unmeasured,
                    $"the build succeeded and its dependency records could not be read, so nothing "
                    + $"established that its objects record the headers they include: {unreadable}"),
                buildDirectory,
                phases,
                rebuilt,
                null);
        }

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

        await RecordInputsAsync(request, buildDirectory, phases, cancellationToken).ConfigureAwait(false);

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
    private async Task<string?> DecideCleanRebuildAsync(
        BuildRequest request,
        string buildDirectory,
        CancellationToken cancellationToken)
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

        // The content comparison first, because it holds whatever the clock did. A source whose
        // bytes differ from the ones this directory was built from is stale however its timestamp
        // reads, and the timestamp is the only thing the build system will consult.
        var changed = await ChangedSinceRecordAsync(request, previous, cancellationToken).ConfigureAwait(false);

        if (changed is not null)
        {
            return changed;
        }

        var newestOutput = NewestOutput(request, buildDirectory);

        if (newestOutput is null)
        {
            return null;
        }

        var stale = await ChangedInputsNotNewerThanAsync(request, newestOutput.Value, cancellationToken)
            .ConfigureAwait(false);

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

            var written = _fileSystem.LastWriteTimeUtc(path);
            newest = newest is null || written > newest ? written : newest;
        }

        return newest;
    }

    /// <summary>
    /// The first tracked source that changed without becoming newer than the build's newest output.
    /// Here the question is only whether the build system would notice, and the build system asks
    /// about times; the content question is asked separately, and first.
    /// </summary>
    /// <remarks>
    /// Over the files git tracks, recursively, and not over whatever happens to sit in the tree's
    /// top directory: a C++ project keeps its sources in subdirectories, so a scan of the root alone
    /// looks at a README and a .gitignore and reports every build clean.
    /// </remarks>
    private async Task<string?> ChangedInputsNotNewerThanAsync(
        BuildRequest request,
        DateTime newestOutput,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in await TrackedInputsAsync(request, cancellationToken).ConfigureAwait(false))
        {
            var path = Path.Combine(request.TreeRoot, relativePath);

            if (!_fileSystem.FileExists(path))
            {
                continue;
            }

            var written = _fileSystem.LastWriteTimeUtc(path);

            if (written >= newestOutput)
            {
                continue;
            }

            if (written.AddSeconds(ClockStepSuspicionSeconds) > newestOutput)
            {
                return relativePath;
            }
        }

        return null;
    }

    /// <summary>
    /// The files git tracks in the leg's tree, or an empty list when git could not be asked.
    /// </summary>
    /// <remarks>
    /// An empty list makes both callers fail closed: the staleness scan finds nothing to clear the
    /// build, and the record written afterwards holds no fingerprint, so the next build cannot
    /// conclude the tree held still and rebuilds from clean.
    /// </remarks>
    private async Task<IReadOnlyList<string>> TrackedInputsAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var index = await _gitClient.ListIndexAsync(request.TreeRoot, cancellationToken).ConfigureAwait(false);

            return [.. index
                .Where(entry => entry.IsRegularFile)
                .Select(entry => entry.Path)
                .Distinct(StringComparer.Ordinal)];
        }
        catch (HarnessException ex)
        {
            _output.Warn(CommandName, $"{request.Leg}: the files git tracks could not be listed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Whether any input's content differs from what this directory was last built from, which is
    /// the question no clock can answer wrongly.
    /// </summary>
    private async Task<string?> ChangedSinceRecordAsync(
        BuildRequest request,
        string previous,
        CancellationToken cancellationToken)
    {
        var recorded = Fingerprints(previous);

        if (recorded.Count == 0)
        {
            // Either the record predates fingerprinting or nothing could be fingerprinted when it
            // was written. Neither says the tree held still, and reading it as though it did is how
            // a stepped clock gets to hand the tests yesterday's object.
            return "the previous build recorded no input fingerprint, so nothing here can say the tree held still";
        }

        var current = await _fingerprints
            .TakeAsync(request.TreeRoot, await TrackedInputsAsync(request, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

        if (current.Unreadable.Count > 0)
        {
            return $"'{current.Unreadable[0].Path}' could not be read, so nothing here can say the tree held still";
        }

        foreach (var file in current.Files)
        {
            if (recorded.TryGetValue(file.Path, out var content) && !content.Equals(file.Content, StringComparison.Ordinal))
            {
                return $"'{file.Path}' differs from what this directory was built from";
            }
        }

        return null;
    }

    /// <summary>The content hash of every input the record names, by path.</summary>
    private static Dictionary<string, string> Fingerprints(string record)
    {
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in record.Split('\n'))
        {
            if (!line.StartsWith(FingerprintMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(' ', FingerprintMarker.Length);

            if (separator > 0)
            {
                fingerprints[line[(separator + 1)..]] = line[FingerprintMarker.Length..separator];
            }
        }

        return fingerprints;
    }

    /// <summary>
    /// Records what this build was built from: whether it spanned a clock step, and a content
    /// fingerprint of every input.
    /// </summary>
    /// <remarks>
    /// The fingerprint is what makes the next build's staleness decision independent of the clock.
    /// Without it the only question that can be asked is whether a source is newer than an object,
    /// which is exactly the question a stepped clock answers wrongly.
    /// </remarks>
    private async Task RecordInputsAsync(
        BuildRequest request,
        string buildDirectory,
        IReadOnlyList<PhaseResult> phases,
        CancellationToken cancellationToken)
    {
        var stepped = phases.Any(phase => phase.ClockStepped);
        var inputs = await TrackedInputsAsync(request, cancellationToken).ConfigureAwait(false);
        var snapshot = await _fingerprints.TakeAsync(request.TreeRoot, inputs, cancellationToken).ConfigureAwait(false);

        var record = new System.Text.StringBuilder()
            .Append(stepped ? ClockStepMarker : "clean").Append('\n')
            .Append(request.Variant.DirectoryName).Append('\n');

        foreach (var file in snapshot.Files)
        {
            record.Append(FingerprintMarker).Append(file.Content).Append(' ').Append(file.Path).Append('\n');
        }

        _fileSystem.WriteAllTextAtomic(Path.Combine(buildDirectory, BuildRecordFileName), record.ToString());
    }

    private List<string> MissingOutputs(BuildRequest request, string buildDirectory)
        => [.. request.Project.BuildOutputs
            .Where(output => !_fileSystem.FileExists(Path.Combine(buildDirectory, output)))];

    /// <summary>
    /// The dependency report for a cmake build, or why it could not be produced.
    /// </summary>
    /// <returns>
    /// The report and no reason where the check ran, no report and no reason where the project is
    /// not one this check applies to, and a reason where the check was attempted and failed.
    /// </returns>
    /// <remarks>
    /// The reason is returned rather than warned about and dropped. The check's whole purpose is to
    /// catch a build directory whose objects record no headers, which is how a stale object survives
    /// a header change; a run that could not perform it and passed anyway reports "no such object
    /// found" when what happened is that nobody looked.
    /// </remarks>
    private async Task<(NinjaDependencyReport? Report, string? Unreadable)> ReadDependenciesAsync(
        BuildRequest request,
        string buildDirectory,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Project.Type, "cmake", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        try
        {
            return (await _dependencyCheck.CheckAsync(buildDirectory, cancellationToken).ConfigureAwait(false), null);
        }
        catch (HarnessException ex)
        {
            _output.Warn(CommandName, $"{request.Leg}: dependency records could not be read: {ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>What the harness records beside a build, so the next one can tell what happened.</summary>
    private const string BuildRecordFileName = ".harness-build";

    /// <summary>What that record says when the build it describes spanned a clock step.</summary>
    private const string ClockStepMarker = "clock-stepped";

    /// <summary>What every fingerprint line in that record starts with.</summary>
    private const string FingerprintMarker = "in ";

    /// <summary>
    /// How far behind the newest output a changed input may be before it is treated as a clock step
    /// rather than an ordinary older file. The measured host steps its clock about 25 seconds.
    /// </summary>
    private const int ClockStepSuspicionSeconds = 60;
}
