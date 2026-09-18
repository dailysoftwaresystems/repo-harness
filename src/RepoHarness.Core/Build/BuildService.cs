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
    bool Time = false)
{
    /// <inheritdoc cref="Hosts.HostReport.ProgramDirectories"/>
    public IReadOnlyList<string> ProgramDirectories { get; init; } = [];

    /// <summary>What the host the leg runs on declares under <c>env</c>, beneath the variant's own environment.</summary>
    public IReadOnlyDictionary<string, string> HostEnvironment { get; init; } = new Dictionary<string, string>();
}

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
    ProcessSampler processSampler,
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
    private readonly ProcessSampler _processSampler = processSampler;
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
        var overlay = request.Variant.Overlay(config, request.Project);

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

        // Opened around the whole span, not around each phase. A build is configure and then build:
        // guards on each separately would fingerprint around one, fingerprint around the other, and
        // miss a file changed in the gap between them — the same moving tree, reported as a pass.
        //
        // Until now nothing watched a build at all. The tree was fingerprinted once, afterwards, to
        // record what the directory had been built from; nothing asked whether it had held still
        // while the compiler read it. A source edited mid-build produced a binary from a tree that
        // never existed, and the leg said passed.
        var (watched, unmeasurable) = await TrackedInputsAsync(request, cancellationToken).ConfigureAwait(false);

        await using var guards = await LegGuards
            .OpenAsync(
                _fingerprints,
                _processSampler,
                new LegGuardRequest
                {
                    TreeRoot = request.TreeRoot,
                    Inputs = unmeasurable is null ? LegInputs.Watch(watched) : LegInputs.Unmeasured(unmeasurable),
                    Contention = ContentionRequests.For(
                        config,
                        request.Leg,
                        buildDirectory,
                        request.TreeRoot,
                        request.PlatformKey),
                },
                cancellationToken)
            .ConfigureAwait(false);

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
                return await FinishAsync(result.Verdict(), null).ConfigureAwait(false);
            }
        }

        if (request.Project.BuildOutputs.Count == 0)
        {
            // Nothing declared is nothing witnessed. A build tool exits 0 having produced nothing
            // often enough — a target filtered out, a generator writing somewhere else — that the
            // exit code alone is not evidence, and an empty list makes the check below vacuously
            // true rather than absent, which reads as a pass nobody performed.
            return await FinishAsync(
                ReachedVerdict.Of(
                    LegVerdict.Unwitnessed,
                    $"project '{request.Project.Name}' declares no buildOutputs, so nothing established that "
                    + "this build produced anything; a zero exit code is not that evidence"),
                null)
                .ConfigureAwait(false);
        }

        var unresolved = request.Project.BuildOutputs
            .Where(output => string.IsNullOrWhiteSpace(output.For(request.PlatformKey)))
            .ToList();

        if (unresolved.Count > 0)
        {
            // Every declared output must name a path here, not merely some of them. An entry that
            // resolves to nothing is dropped from the list this build is held to, so a project
            // declaring three and resolving two passes having checked two thirds of its own evidence
            // with nothing saying so, and one resolving none passes having checked nothing at all.
            // The configuration reader refuses an entry naming no path for a platform some leg
            // builds on, and a leg only runs on a host whose system it matches, so reaching here
            // means one of those two stopped holding. Refused anyway: the cost of being wrong is a
            // green build nobody witnessed, which is the whole of what buildOutputs is for.
            return await FinishAsync(
                ReachedVerdict.Of(
                    LegVerdict.Unwitnessed,
                    $"project '{request.Project.Name}' declares {unresolved.Count} buildOutput(s) naming no "
                    + $"path for '{request.PlatformKey}', so this build would be held to fewer files than "
                    + $"the configuration declares: {string.Join("; ", unresolved)}"),
                null)
                .ConfigureAwait(false);
        }

        var missing = ExpectedOutputs(request, buildDirectory)
            .Where(path => !_fileSystem.FileExists(path))
            .ToList();

        if (missing.Count > 0)
        {
            // A build tool can exit 0 having produced nothing, and the tests then run against a
            // binary left over from an earlier build. Reported as unwitnessed rather than failed:
            // the build reported success, and what is missing is the evidence, not the exit code.
            return await FinishAsync(
                ReachedVerdict.Of(
                    LegVerdict.Unwitnessed,
                    $"the build exited 0 and produced none of: {string.Join(", ", missing)}"),
                null)
                .ConfigureAwait(false);
        }

        var (dependencies, unreadable) = await ReadDependenciesAsync(request, buildDirectory, BuildAdapters.EnvironmentFor(overlay, request), cancellationToken)
            .ConfigureAwait(false);

        if (unreadable is not null)
        {
            // Not a pass. The check exists because an object with no recorded headers is never
            // rebuilt when a header changes, and a check that could not run has established nothing
            // about that — which is what unmeasured means, and is a different fact from "clean".
            return await FinishAsync(
                ReachedVerdict.Of(
                    LegVerdict.Unmeasured,
                    $"the build succeeded and its dependency records could not be read, so nothing "
                    + $"established that its objects record the headers they include: {unreadable}"),
                null)
                .ConfigureAwait(false);
        }

        if (dependencies is { IsClean: false })
        {
            // An object with no recorded header dependencies is never rebuilt when a header it
            // includes changes, so the next build links yesterday's object and reports success.
            return await FinishAsync(
                ReachedVerdict.Of(
                    LegVerdict.Failed,
                    $"{dependencies.WithoutHeaders.Count} object(s) recorded no header dependencies, "
                    + $"including {string.Join(", ", dependencies.WithoutHeaders.Take(3))}"),
                dependencies)
                .ConfigureAwait(false);
        }

        return await FinishAsync(ReachedVerdict.Of(LegVerdict.Passed), dependencies).ConfigureAwait(false);

        // Closes the guards, folds what they saw into the verdict, and records what this build was
        // built from. Local because every exit from this method has to do all three: a build that
        // returned early without closing them would leave a sampler running and report a verdict
        // nothing watched.
        async Task<BuildResult> FinishAsync(ReachedVerdict reached, NinjaDependencyReport? dependencies)
        {
            var seen = await guards.CloseAsync(cancellationToken).ConfigureAwait(false);
            var verdict = seen.Decide(request.Leg, [reached]);

            ContentionWarnings.Write(_output, CommandName, request.Leg, seen.Contention!, config.Contention);
            WarnWhenDeeperThanTheReserve(request, buildDirectory, config.Worktrees.PathBudgetReserve);

            // Recorded for a build that reached a verdict on its own terms, and marked as
            // untrustworthy when anything doubted it. The record is what the next build's staleness
            // decision reads: written after a moving tree it would describe the tree as it ended up,
            // and the next build would compare cleanly against objects compiled from the tree as it
            // began.
            //
            // Contention counts as doubt for the same reason. A build that shared its directory with
            // another one wrote no record at all until now, so the previous build's clean record
            // survived it and the obvious next step — run it again, having changed nothing — built
            // incrementally on top of objects this tool had just called untrustworthy.
            if (verdict.Verdict == LegVerdict.Passed
                || seen.Inputs?.Verdict() is not null
                || seen.Contention?.Verdict() is not null)
            {
                await RecordInputsAsync(request, buildDirectory, phases, seen, watched, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new BuildResult(verdict, buildDirectory, phases, rebuilt, dependencies);
        }
    }

    /// <summary>
    /// Warns when this build produced a path deeper below its build directory than
    /// worktrees.pathBudgetReserve declares, naming both numbers.
    /// </summary>
    /// <param name="request">The leg's build.</param>
    /// <param name="buildDirectory">Where it built.</param>
    /// <param name="reserve">What worktrees.pathBudgetReserve declares.</param>
    /// <remarks>
    /// What keeps the reserve honest. It is a number somebody measured once, against whatever the
    /// build produced then; headers grow deeper and generators rename what they write, and nothing
    /// else would notice until a worktree's build failed with compile errors in files it never
    /// touched. Measured after every build, from the files the build itself left, and said when it
    /// could not be measured rather than taken as fine.
    /// </remarks>
    private void WarnWhenDeeperThanTheReserve(BuildRequest request, string buildDirectory, int reserve)
    {
        if (!_fileSystem.DirectoryExists(buildDirectory))
        {
            return;
        }

        var longest = 0;
        var deepest = string.Empty;

        try
        {
            foreach (var file in _fileSystem.EnumerateFiles(buildDirectory, recursive: true))
            {
                var below = Path.GetRelativePath(buildDirectory, file);

                if (below.Length > longest)
                {
                    longest = below.Length;
                    deepest = below;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.Warn(
                CommandName,
                $"{request.Leg}: the deepest path this build produced could not be measured, so "
                + $"worktrees.pathBudgetReserve was not checked against it: {ex.Message}");

            return;
        }

        if (longest > reserve)
        {
            _output.Warn(
                CommandName,
                $"{request.Leg}: this build produced a path {longest} characters long below its build "
                + $"directory ('{deepest}'), and worktrees.pathBudgetReserve declares {reserve}. Raise it "
                + $"to at least {longest}, or a worktree created against it may not leave its build room.");
        }
    }

    /// <summary>
    /// Whether this variant must be rebuilt from clean, and why.
    /// </summary>
    /// <remarks>
    /// Ninja, Make and MSBuild decide what is stale by ordering timestamps, which a stepped clock
    /// defeats without a word: an object stamped during a forward step looks newer than a source
    /// edited just after it. Six conditions rebuild the variant from clean, and the ledger names
    /// which.
    /// <para>
    /// Three are answers: the previous build spanned a clock step; an input's content differs from
    /// what this directory was built from; or a changed input is not newer than the newest output,
    /// which is what a stepped clock does and what a build system comparing timestamps would miss.
    /// </para>
    /// <para>
    /// Three are the absence of an answer, and rebuild for that reason alone: there is no record to
    /// compare, there is no set to compare because the tracked files could not be listed, or an
    /// input could not be read. None of them says the tree held still, and a stale binary reported
    /// as a pass is the one price an incremental build must never pay.
    /// </para>
    /// <para>
    /// The order matters and is the reason the input set can be narrowed at all. The clock step is
    /// tested first, so a host whose clock moves rebuilds from clean whatever the set says; what is
    /// left is builds where no step occurred, and there the build system's own dependency graph is
    /// authoritative.
    /// </para>
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
            return "a clock step: the previous build spanned one, so nothing it stamped can be ordered";
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
            : $"a timestamp a build system would miss: '{stale}' changed but is not newer than the "
                + "newest output, which a stepped clock does";
    }

    private DateTime? NewestOutput(BuildRequest request, string buildDirectory)
    {
        DateTime? newest = null;

        foreach (var path in ExpectedOutputs(request, buildDirectory))
        {
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
    /// Every file this build must produce, resolved for the platform it ran on.
    /// </summary>
    /// <remarks>
    /// An entry naming no path for this platform contributes none, rather than contributing an
    /// empty one: that would resolve to the build directory itself, which <c>FileExists</c> never
    /// finds, failing a build that produced everything it actually named. The caller refuses a
    /// project with any such entry, so this never silently shortens the list of files a build is
    /// held to. What is reported afterwards is the resolved path rather than the entry as written:
    /// an entry naming one file per platform would otherwise report every platform's spelling,
    /// leaving the reader to work out which this leg actually missed.
    /// </remarks>
    /// <param name="request">The build, carrying the platform it ran on.</param>
    /// <param name="buildDirectory">The directory the paths are relative to.</param>
    private static IEnumerable<string> ExpectedOutputs(BuildRequest request, string buildDirectory)
        => request.Project.BuildOutputs
            .Select(output => output.For(request.PlatformKey))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(buildDirectory, path!));

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
        var (scanned, unlistable) = await TrackedInputsAsync(request, cancellationToken).ConfigureAwait(false);

        if (unlistable is not null)
        {
            // The set this would have scanned could not be established, so "nothing is newer" is an
            // answer about no files. Handled as the content comparison handles it, and not discarded.
            return $"no set to scan: {unlistable}";
        }

        foreach (var relativePath in scanned)
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
    /// The tracked files whose content decides whether this variant's build directory can be kept,
    /// or an empty list when git could not be asked.
    /// </summary>
    /// <remarks>
    /// An empty list makes both callers fail closed: the staleness scan finds nothing to clear the
    /// build, and the record written afterwards holds no fingerprint, so the next build cannot
    /// conclude the tree held still and rebuilds from clean.
    /// <para>
    /// Narrowed to the files that can actually affect this build, by what the project declares or
    /// else by what its type reads. Every tracked file was the first answer and it is the safe one,
    /// but it makes a documentation edit discard a warm build directory on every leg — measured on
    /// a consumer's tree, one markdown file put two legs through a full rebuild. Why narrowing
    /// cannot hand anybody a stale binary is set out on <see cref="BuildInputKinds"/>.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<string> Inputs, string? Unmeasurable)> TrackedInputsAsync(
        BuildRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> tracked;

        try
        {
            var index = await _gitClient.ListIndexAsync(request.TreeRoot, cancellationToken).ConfigureAwait(false);

            tracked = [.. index
                .Where(entry => entry.IsRegularFile)
                .Select(entry => entry.Path)
                .Distinct(StringComparer.Ordinal)];
        }
        catch (HarnessException ex)
        {
            // Told apart from a set that is legitimately empty, which the caller reads as nothing
            // to watch. Nobody asked git and git said nothing are different facts, and only one of
            // them means the tree can be said to have held still.
            _output.Warn(CommandName, $"{request.Leg}: the files git tracks could not be listed: {ex.Message}");
            return ([], $"the files git tracks in '{request.TreeRoot}' could not be listed: {ex.Message}");
        }

        // What the project says replaces what its type reads, and only when it says something:
        // an empty list is a project that declared the key and left it blank, which is not a
        // statement that nothing affects its build.
        var kinds = request.Project.RebuildableFormats.Count > 0
            ? new BuildInputKinds(request.Project.RebuildableFormats)
            : BuildAdapters.For(request.Project.Type).InputKinds;

        if (!kinds.Narrows)
        {
            return (tracked, null);
        }

        var narrowed = tracked.Where(kinds.Covers).ToList();

        // A narrowing that removes every tracked file leaves nothing to watch, and nothing to watch
        // is reported as a span with nothing to say about it. That is right for a repository that
        // genuinely tracks none of its language's files and wrong for a list with a typo in it, and
        // the two are indistinguishable from here — so it is said rather than decided. Said, because
        // a guard that is off must never be off quietly.
        if (narrowed.Count == 0 && tracked.Count > 0)
        {
            var source = request.Project.RebuildableFormats.Count > 0
                ? "this project's rebuildableFormats"
                : $"the kinds a '{request.Project.Type}' project reads";

            _output.Warn(
                CommandName,
                $"{request.Leg}: none of the {tracked.Count} tracked file(s) is a build input by "
                + $"{source}, so nothing was watched while this built and nothing here can say the "
                + "tree held still.");
        }

        return (narrowed, null);
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
            return "no record to compare: the previous build recorded no input fingerprint, so "
                + "nothing here can say the tree held still";
        }

        var (compared, unlistable) = await TrackedInputsAsync(request, cancellationToken).ConfigureAwait(false);

        if (unlistable is not null)
        {
            return $"no set to compare: {unlistable}";
        }

        var current = await _fingerprints
            .TakeAsync(request.TreeRoot, compared, cancellationToken)
            .ConfigureAwait(false);

        if (current.Unreadable.Count > 0)
        {
            return $"an unreadable input: '{current.Unreadable[0].Path}' could not be read, so "
                + "nothing here can say the tree held still";
        }

        foreach (var file in current.Files)
        {
            if (recorded.TryGetValue(file.Path, out var content) && !content.Equals(file.Content, StringComparison.Ordinal))
            {
                return $"changed content: '{file.Path}' differs from what this directory was built from";
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
        LegGuardReport seen,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        // A tree that moved under the build is the same problem as a clock that stepped: what this
        // directory holds was compiled from a tree the fingerprint below does not describe. The
        // marker already means "nothing this build stamped can be ordered", and that is exactly
        // what is true here, so the next build starts from clean and says why.
        var snapshot = await _fingerprints.TakeAsync(request.TreeRoot, inputs, cancellationToken).ConfigureAwait(false);

        // An input that could not be read leaves no line in the record, and a path the next build
        // cannot find in the record is one its content comparison skips — so the file that was
        // unreadable here is exactly the file that would go unchecked there. Marked instead, which
        // costs one clean rebuild and cannot hand anybody a stale object.
        var stepped = phases.Any(phase => phase.ClockStepped)
            || seen.Inputs?.Verdict() is not null
            || seen.Contention?.Verdict() is not null
            || snapshot.Unreadable.Count > 0;

        var record = new System.Text.StringBuilder()
            .Append(stepped ? ClockStepMarker : "clean").Append('\n')
            .Append(request.Variant.DirectoryName).Append('\n');

        foreach (var file in snapshot.Files)
        {
            record.Append(FingerprintMarker).Append(file.Content).Append(' ').Append(file.Path).Append('\n');
        }

        _fileSystem.WriteAllTextAtomic(Path.Combine(buildDirectory, BuildRecordFileName), record.ToString());
    }

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
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Project.Type, "cmake", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        try
        {
            // The ninja the build itself ran, which only its own environment may have found - and
            // started in that environment, so one looked up by name is found on the PATH the build's
            // phases had, a host's own among them.
            var recorded = _buildDirectoryGuard.Read(buildDirectory)?.MakeProgram;

            return (await _dependencyCheck.CheckAsync(buildDirectory, request.ProgramDirectories, recorded, environment, cancellationToken).ConfigureAwait(false), null);
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
