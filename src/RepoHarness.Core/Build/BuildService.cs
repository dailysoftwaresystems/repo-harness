using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
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

    /// <summary>
    /// What the host the leg runs on gives it, beneath the variant's own environment: what the host
    /// declares under <c>env</c>, with the developer environment the toolchain names set up over it.
    /// </summary>
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
    NinjaDependencyReport? Dependencies)
{
    /// <summary>
    /// The compilers CMake configured the build with, by language, as it reported them after
    /// configuring; none for a build CMake does not configure, or where it reported nothing.
    /// </summary>
    public IReadOnlyList<CompilerFact> Compilers { get; init; } = [];

    /// <summary>
    /// What a leg's line says about this build beyond its verdict: why it started from clean, and each
    /// phase that spanned a clock step.
    /// </summary>
    /// <remarks>
    /// The same lines whichever command built: a test run or a runner that builds first reported its
    /// verdict and dropped these, so a leg rebuilt from clean on the way to its tests said so only in
    /// the progress lines, and never in its ledger or its JSON.
    /// </remarks>
    public IReadOnlyList<string> Notes =>
    [
        .. RebuiltFromClean is { } reason ? ["rebuilt from clean: " + reason] : Array.Empty<string>(),
        .. Phases
            .Where(phase => phase.ClockStepped)
            .Select(phase => $"{phase.Phase} spanned a clock step, so its duration and every mtime it wrote are suspect"),
    ];

    /// <summary>The last lines the phase the build stopped at printed, as <see cref="PhaseResult.TailOf"/> picks them.</summary>
    public IReadOnlyList<string> Tail => PhaseResult.TailOf(Phases);
}

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
    CMakeToolchainReader toolchainReader,
    NinjaDependencyCheck dependencyCheck,
    CompilerVersionProbe compilerVersions,
    InputFingerprint fingerprints,
    ProcessSampler processSampler,
    Git.IGitClient gitClient,
    IFileSystem fileSystem,
    IHarnessOutput output,
    TimeProvider? wallClock = null) : IBuildService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "build";

    private readonly PhaseRunner _phaseRunner = phaseRunner;
    private readonly BuildDirectoryGuard _buildDirectoryGuard = buildDirectoryGuard;
    private readonly CMakeToolchainReader _toolchainReader = toolchainReader;
    private readonly NinjaDependencyCheck _dependencyCheck = dependencyCheck;
    private readonly CompilerVersionProbe _compilerVersions = compilerVersions;
    private readonly InputFingerprint _fingerprints = fingerprints;
    private readonly ProcessSampler _processSampler = processSampler;
    private readonly Git.IGitClient _gitClient = gitClient;

    /// <summary>The clock this build dates its own work by, so that what it wrote is told from what it found.</summary>
    private readonly TimeProvider _wallClock = wallClock ?? TimeProvider.System;
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

        // Made once, and read by everything that asks which compiler this build uses: the guard, every
        // phase and the dependency check. A compiler the host's env names is the one the build starts
        // wherever the variant names none, and a guard reading the variant alone let a directory CMake
        // had configured with another be reused, the leg passing on a compiler nobody chose.
        var environment = BuildAdapters.EnvironmentFor(overlay, request);

        // Before configuring, not after: a directory configured from another worktree watches that
        // tree's sources, which produced both a false refusal and a silent wrong answer. Compared
        // against the directory the build is actually configured from, which is the project's own
        // path below the tree root and not the tree root itself.
        _buildDirectoryGuard.Check(
            buildDirectory,
            Path.Combine(request.TreeRoot, request.Project.Path),
            CompilerValue.For(CompilerValue.C, overlay.CacheVars, environment),
            CompilerValue.For(CompilerValue.Cxx, overlay.CacheVars, environment),
            adapter.BuildTypeOf(config, request.Variant.Config),
            PathSearch.For(environment, request.ProgramDirectories));

        // Listed once, for the decision below and for the guards after it: the tracked files that can
        // affect this build, or why they could not be listed.
        var (watched, unmeasurable) = await TrackedInputsAsync(request, cancellationToken).ConfigureAwait(false);

        // A compiler changed since CMake identified it comes first: no record of the tree can answer
        // for it, since CMake never identifies a cached compiler again.
        var replaced = adapter is CMakeAdapter
            ? await CompilerReplacedAsync(request, buildDirectory, environment, cancellationToken).ConfigureAwait(false)
            : null;

        var (rebuilt, taken) = replaced is not null
            ? (replaced, null)
            : await DecideCleanRebuildAsync(request, buildDirectory, watched, unmeasurable, cancellationToken).ConfigureAwait(false);

        if (rebuilt is not null)
        {
            _output.Info(CommandName, $"{request.Leg}: rebuilding from clean, because {rebuilt}");
            _fileSystem.DeleteDirectory(buildDirectory);
        }

        _fileSystem.CreateDirectory(buildDirectory);

        IReadOnlyList<CompilerFact> compilers = [];

        var phases = new List<PhaseResult>();

        // Opened around the whole span, not around each phase. A build is configure and then build:
        // guards on each separately would fingerprint around one, fingerprint around the other, and
        // miss a file changed in the gap between them — the same moving tree, reported as a pass.
        //
        // Until now nothing watched a build at all. The tree was fingerprinted once, afterwards, to
        // record what the directory had been built from; nothing asked whether it had held still
        // while the compiler read it. A source edited mid-build produced a binary from a tree that
        // never existed, and the leg said passed.
        //
        // Opened on the snapshot the decision took where it kept the directory, rather than on another
        // taken moments later: a file changed between the two would be what this build records it was
        // given, and no reading would ever have compared or dated it. Where the directory was discarded
        // there is nothing to compare, and the guards read the tree afresh once the delete is done: on the
        // decision's reading, a file edited while a large directory was deleted would count as moving under
        // a build that had not begun, and one unreadable only as the decision read it would leave a clean
        // build unmeasured and its record marked, for the next build to start from clean again.
        await using var guards = await LegGuards
            .OpenAsync(
                _fingerprints,
                _processSampler,
                new LegGuardRequest
                {
                    TreeRoot = request.TreeRoot,
                    Inputs = unmeasurable is null ? LegInputs.Watch(watched) : LegInputs.Unmeasured(unmeasurable),
                    Opening = rebuilt is null ? taken : null,
                    Contention = ContentionRequests.For(
                        config,
                        request.Leg,
                        buildDirectory,
                        request.TreeRoot,
                        request.PlatformKey),
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Recorded before the build system runs, from the tree it is about to read and with when each
        // input had last been written, and again as the build ends. A build that fails, or is stopped
        // by its caller's time limit, still leaves objects compiled from this tree: dated against an
        // earlier record, every change made before it would look like one a build system could miss,
        // and a build stopped for running long would start from clean every time and never finish.
        var recorded = Began(request, guards.Opening);

        // When this build began touching the directory, so that what it wrote there can be told from what
        // it found. A directory kept between builds holds objects of targets that no longer exist - a test
        // renamed shorter leaves the old name's object behind - and the deepest path in it is often one of
        // those, which this build did not produce and raising a reserve would not affect.
        var began = _wallClock.GetUtcNow().UtcDateTime;

        Record(buildDirectory, recorded);

        // Asked of every configure, so the compilers a verdict names are the ones this build's
        // configure resolved, never the ones an earlier one did. Asked once the record is written, so
        // the record is the first thing a build puts in its directory: a build stopped before writing it
        // leaves nothing else behind to be read as a build nobody recorded.
        var asked = adapter is CMakeAdapter ? _toolchainReader.Ask(buildDirectory) : null;

        foreach (var phase in adapter.Phases(config, request, buildDirectory, overlay, environment))
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

            // Marked the moment a phase says its clock stepped, not only as the build ends: a build
            // stopped after this phase would otherwise leave a record that says nothing of it.
            if (recorded.Unordered is null && Stepped(phases) is { } stepped)
            {
                recorded = recorded with { Unordered = stepped };
                Record(buildDirectory, recorded);
            }

            if (asked is not null && string.Equals(phase.Phase, CMakeAdapter.ConfigurePhase, StringComparison.Ordinal))
            {
                // Read whether or not configure passed, and only what this configure answered: one that
                // failed names no compiler rather than the one before it. Held to the toolchain before
                // anything is built with it, so no object comes from a compiler nobody chose.
                var reading = _toolchainReader.Read(buildDirectory, asked);
                compilers = reading.Compilers;

                if (result.Passed && CompilerFacts.HeldTo(config, request.Variant.Toolchain, reading) is { } contradicted)
                {
                    return await FinishAsync(contradicted, null).ConfigureAwait(false);
                }
            }

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

        var (dependencies, unreadable) = await ReadDependenciesAsync(request, buildDirectory, environment, cancellationToken)
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
            var left = Survey(buildDirectory, began);

            ContentionWarnings.Write(_output, CommandName, request.Leg, seen.Contention!, config.Contention);
            WarnWhenDeeperThanTheReserve(request, left, config.Worktrees.PathBudgetReserve, phases);

            // Recorded again whatever the verdict, with the newest file the build left: the next build
            // dates its changes against that, not against the directory as it stands by then, which
            // holds whatever a test run wrote there since - ctest's own logs among it - and an edit made
            // while the tests ran would be dated behind them. A failed build leaves what it compiled as
            // surely as a passing one. Marked as unordered, with why, where anything doubted it, so the
            // next build starts from clean rather than build on objects nobody can match to a tree.
            // Contention is doubt too: a build that shared its directory once wrote no record at all, so
            // the previous build's clean one survived it, and running it again with nothing changed
            // built on top of objects this tool had just called untrustworthy.
            Record(buildDirectory, recorded with
            {
                Unordered = recorded.Unordered ?? Unordered(phases, seen, guards.Opening),
                Newest = left.Newest,
            });

            return new BuildResult(verdict, buildDirectory, phases, rebuilt, dependencies) { Compilers = compilers };
        }
    }

    /// <summary>
    /// Warns when this build produced a path deeper below its build directory than
    /// worktrees.pathBudgetReserve declares, naming both numbers.
    /// </summary>
    /// <param name="request">The leg's build.</param>
    /// <param name="left">What it left in its build directory.</param>
    /// <param name="reserve">What worktrees.pathBudgetReserve declares.</param>
    /// <param name="phases">The phases it ran, which say whether its dates can be ordered at all.</param>
    /// <remarks>
    /// What keeps the reserve honest. It is a number somebody measured once, against whatever the
    /// build produced then; headers grow deeper and generators rename what they write, and nothing
    /// else would notice until a worktree's build failed with compile errors in files it never
    /// touched. Measured after every build, from the files the build itself left, and said when it
    /// could not be measured rather than taken as fine.
    /// </remarks>
    private void WarnWhenDeeperThanTheReserve(BuildRequest request, Left left, int reserve, IReadOnlyList<PhaseResult> phases)
    {
        if (left.Unreadable is { } unreadable)
        {
            _output.Warn(
                CommandName,
                $"{request.Leg}: the deepest path this build produced could not be measured, so "
                + $"worktrees.pathBudgetReserve was not checked against it: {unreadable}");

            return;
        }

        // Which build wrote a file is read from its date, and a clock that stepped during this one puts
        // that beyond saying: an object stamped inside the step can date before a build that preceded it.
        // Said as not checked, rather than guessed at, because both readings are actionable and wrong.
        if (phases.FirstOrDefault(phase => phase.ClockStepped) is { } stepped)
        {
            var longest = left.Deepest.Length >= left.Leftover.Length ? left.Deepest : left.Leftover;

            if (longest.Length > reserve)
            {
                _output.Warn(
                    CommandName,
                    $"{request.Leg}: the deepest path below this build directory is {longest.Length} characters "
                    + $"long ('{longest}'), and worktrees.pathBudgetReserve declares {reserve}. This build's "
                    + $"'{stepped.Phase}' phase spanned a clock step, so whether this build wrote it cannot be told "
                    + "from its date.");
            }

            return;
        }

        // The budget is about what a build directory can come to hold, so the deepest path in it is the
        // measure whether this build wrote it or an earlier one did: an object no longer recompiled is
        // still there, and still as long, when a worktree is created against the reserve.
        var deepest = left.Deepest.Length >= left.Leftover.Length ? left.Deepest : left.Leftover;

        if (deepest.Length <= reserve)
        {
            return;
        }

        // Whether this build wrote it is said, and nothing more is claimed. An incremental build recompiles
        // almost nothing, so most of what is there is older than it every time; a date cannot tell an object
        // of a target that still exists from one of a target renamed away, and a consumer was told to raise
        // the reserve for an object that no target owned - which their own budget arithmetic had no room for.
        var whose = left.Deepest.Length >= left.Leftover.Length
            ? "this build wrote it"
            : "this build did not write it, so it is either a target this build had no reason to rebuild, or "
                + "one that no longer exists - if no target owns it, start this variant's build directory from "
                + "clean instead";

        _output.Warn(
            CommandName,
            $"{request.Leg}: the deepest path below this build directory is {deepest.Length} characters long "
            + $"('{deepest}'), and worktrees.pathBudgetReserve declares {reserve}. {char.ToUpperInvariant(whose[0])}{whose[1..]}. "
            + $"Raise the reserve to at least {deepest.Length} where a target owns that path, or a worktree "
            + "created against it may not leave its build room.");
    }

    /// <summary>What a build left in its directory, as one walk of it found.</summary>
    /// <param name="Deepest">
    /// The longest path below the directory that this build wrote, as the host spells it; empty where it
    /// wrote none. Where the walk was not given when the build began, every path counts as its own.
    /// </param>
    /// <param name="Newest">
    /// The newest file in it, relative to it with forward slashes, and when it was written;
    /// <see langword="null"/> where it holds nothing or could not be read.
    /// </param>
    /// <param name="Unreadable">Why the directory could not all be read, where it could not.</param>
    /// <param name="Leftover">
    /// The longest path below the directory that this build did not write, as the host spells it; empty
    /// where every path is this build's, or where the walk was not given when the build began.
    /// </param>
    private sealed record Left(string Deepest, WrittenFile? Newest, string? Unreadable, string Leftover = "");

    /// <summary>
    /// Walks <paramref name="buildDirectory"/> once for what is in it: the deepest path below it and the
    /// newest file.
    /// </summary>
    /// <remarks>
    /// Every file, rather than the declared outputs or the record written last. A host whose clock
    /// steps forward for a moment and back - measured, 25 seconds for 200 milliseconds - stamps what
    /// it writes in that moment ahead of what it writes after, and a phase that starts and ends
    /// outside the step measures no drift: an object compiled in one is dated after the binary linked
    /// from it and after the record. An input is newer than every output only if it is newer than
    /// this. Each file is dated by when it was written there, and a link as itself, never as what it
    /// points at: a build that links its test data in from the source tree would otherwise date every
    /// edit to that data against the edit itself. A directory reached through a link is never walked.
    /// </remarks>
    /// <param name="buildDirectory">The directory to walk.</param>
    /// <param name="began">
    /// When this build began touching the directory, which sorts what it wrote from what it found, or
    /// <see langword="null"/> to take every path as this build's - for a walk made for the newest file
    /// alone, where nothing asks which build wrote it.
    /// </param>
    private Left Survey(string buildDirectory, DateTime? began = null)
    {
        if (!_fileSystem.DirectoryExists(buildDirectory))
        {
            return new Left(string.Empty, null, null);
        }

        // A filesystem that dates coarsely - FAT to two seconds - can stamp a file this build wrote just
        // before the instant recorded. Counted as this build's, because "raise the reserve" is the
        // actionable warning of the two and the one that must not be missed.
        var mine = began?.AddSeconds(-2);
        var deepest = string.Empty;
        var leftover = string.Empty;
        WrittenFile? newest = null;

        try
        {
            foreach (var file in _fileSystem.EnumerateWrittenFiles(buildDirectory))
            {
                var below = Path.GetRelativePath(buildDirectory, file.Path);

                if (mine is null || file.LastWriteTimeUtc >= mine)
                {
                    if (below.Length > deepest.Length)
                    {
                        deepest = below;
                    }
                }
                else if (below.Length > leftover.Length)
                {
                    leftover = below;
                }

                if (newest is null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                {
                    newest = new WrittenFile(below.Replace(Path.DirectorySeparatorChar, '/'), file.LastWriteTimeUtc);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Left(string.Empty, null, ex.Message);
        }

        return new Left(deepest, newest, null, leftover);
    }

    /// <summary>
    /// Why this variant must start from clean because a compiler CMake identified for it is not what is
    /// at its path now, or <see langword="null"/> where each is as CMake identified it or could not be
    /// asked.
    /// </summary>
    /// <param name="request">The leg's build.</param>
    /// <param name="buildDirectory">Where it builds.</param>
    /// <param name="environment">The environment its phases start in, a developer environment's among it.</param>
    /// <param name="cancellationToken">Stops a compiler being asked.</param>
    /// <remarks>
    /// CMake identifies a cached compiler once, when the directory is first configured, and loads that
    /// record on every configure after; a build system has no edge on the compiler itself. So a compiler
    /// updated in place - the same path, another version, as a Visual Studio update rewrites cl.exe where
    /// it stands - builds in a directory still recorded as the old one's, and a consumer's first build
    /// after one failed every precompiled header it had with C1853, "from a different version of the
    /// compiler". Only a directory configured afresh identifies it again. A compiler that cannot be asked
    /// is said and passed over: the question exists to name the cause of a failure the build would show
    /// anyway, and one that cannot be put is no reason to discard a warm directory.
    /// </remarks>
    private async Task<string?> CompilerReplacedAsync(
        BuildRequest request,
        string buildDirectory,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var (identified, unread) = _toolchainReader.Identified(buildDirectory);

        foreach (var why in unread)
        {
            _output.Warn(CommandName, $"{request.Leg}: whether a compiler changed since CMake identified it could not be asked: {why}");
        }

        foreach (var compiler in identified)
        {
            var answer = await _compilerVersions
                .AskAsync(compiler, Path.Combine(request.RunDirectory, request.Leg), environment, request.ProgramDirectories, cancellationToken)
                .ConfigureAwait(false);

            // An id whose version this build does not know how to put together, as CMake's own formula does,
            // or no id at all, as CMake records a compiler it could not identify.
            if (answer is null)
            {
                continue;
            }

            // The id and the version, where CMake recorded one: a record may identify a kind and leave its version out.
            var identity = string.Join(' ', new[] { compiler.Id, compiler.Version }.Where(part => part.Length > 0));
            var identifiedAs = $"CMake identified {compiler.Language}'s compiler, '{compiler.Program}', as {identity}";

            if (answer.Replaced is { } replaced)
            {
                return $"a changed compiler: {identifiedAs}, and {replaced}; CMake identifies a cached compiler once, so "
                    + "only a directory configured afresh builds with what is there now";
            }

            if (answer.Unanswered is { } unanswered)
            {
                _output.Warn(
                    CommandName,
                    $"{request.Leg}: whether {compiler.Language}'s compiler is still the {identity} CMake identified could not "
                    + $"be asked: {unanswered}");

                continue;
            }

            if (!string.Equals(answer.Version, compiler.Version, StringComparison.Ordinal))
            {
                return $"a changed compiler: {identifiedAs}, and it is {answer.Version} now; CMake identifies a cached "
                    + "compiler once, so only a directory configured afresh builds with it";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this variant must be rebuilt from clean, and why; with the snapshot of its inputs the
    /// decision took, where it took one, for the guards to open with where the directory is kept.
    /// </summary>
    /// <remarks>
    /// Ninja, Make and MSBuild decide what is stale by ordering timestamps, which a stepped clock
    /// defeats without a word: an object stamped during a forward step looks newer than a source
    /// edited just after it. Seven conditions rebuild the variant from clean, and the ledger names
    /// which.
    /// <para>
    /// Two are answers. The previous build was marked unordered: a phase spanned a clock step, an
    /// input could not be read as it began, its inputs moved while it ran, or something else may have
    /// used its directory. Or an input changed since that build began is dated no later than the
    /// newest file it left, which is what a stepped clock leaves, and a tool that keeps a file's old
    /// time, and what a build system comparing timestamps could miss.
    /// </para>
    /// <para>
    /// Five are the absence of an answer, and rebuild for that reason alone: there is no record to
    /// compare - one that holds no fingerprint, or none at all beside files a build left - there is no
    /// set to compare because the tracked files could not be listed, an input could not be read, a
    /// changed input could not be dated, or the directory could not be read: for its newest file,
    /// after a build that never finished, or for whether it holds anything, where it holds no record.
    /// None of them says the tree held still, and a stale binary reported as a pass is the one price
    /// an incremental build must never pay. A directory that holds nothing, or is not there, has
    /// nothing to discard.
    /// </para>
    /// <para>
    /// An input changed since, and dated since, is the build system's to act on, and nothing here
    /// rebuilds for it. Dated after everything that build left, it is newer than every output that
    /// reads it, which is the question the build system asks; and a CMake project is configured on
    /// every build, so what its configure reads is read again. An output made from a file its rule
    /// does not name - a custom command reading a file it lists in no DEPENDS - is outside that
    /// question, and is remade when its rule is. Rebuilding from clean for every change was the first
    /// answer, and it put a consumer through 1,186 steps from nothing for one edit to one input - a
    /// build that ran past its caller's time limit and left the directory half built for whatever
    /// read it next.
    /// </para>
    /// <para>
    /// A build that finished recorded the newest file it left, and its guards vouched for the tree
    /// while it ran. One that never finished - failed hard, or stopped - recorded neither, so its
    /// directory is read for its newest file, and an input written again since it began counts as
    /// changed, its content though it held: a stash and its pop leave one exactly as it was, having
    /// let the compiler read something else in between. An input deleted since is the build
    /// system's, which sees an input gone without asking its date; and one the record never held -
    /// new to the set since, as a file first committed after the build that compiled it is - is judged
    /// by the build system alone, as every file outside the set is.
    /// </para>
    /// <para>
    /// The order matters and is the reason the input set can be narrowed at all. The unordered mark
    /// is read first, so a host whose clock moves rebuilds from clean whatever the set says; what is
    /// left is builds where no step occurred, and there the build system's own dependency graph is
    /// authoritative.
    /// </para>
    /// </remarks>
    private async Task<(string? Rebuilt, InputSnapshot? Taken)> DecideCleanRebuildAsync(
        BuildRequest request,
        string buildDirectory,
        IReadOnlyList<string> inputs,
        string? unlistable,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(buildDirectory, BuildRecord.FileName);

        if (!_fileSystem.FileExists(path))
        {
            return (Unaccounted(buildDirectory), null);
        }

        var previous = BuildRecord.Parse(_fileSystem.ReadAllText(path));

        if (previous.Unordered is { } unordered)
        {
            return (unordered, null);
        }

        if (previous.Contents.Count == 0)
        {
            // Either the record predates fingerprinting or nothing could be fingerprinted when it
            // was written. Neither says the tree held still, and reading it as though it did is how
            // a stepped clock gets to hand the tests yesterday's object.
            return ("no record to compare: the previous build recorded no input fingerprint - git tracked "
                + "none of its inputs, or it predates fingerprinting - so nothing here can say the tree held "
                + "still", null);
        }

        if (unlistable is not null)
        {
            return ($"no set to compare: {unlistable}", null);
        }

        var current = await _fingerprints.TakeAsync(request.TreeRoot, inputs, cancellationToken).ConfigureAwait(false);

        if (current.Unreadable.Count > 0)
        {
            return ($"an unreadable input: '{current.Unreadable[0].Path}' could not be read, so "
                + "nothing here can say the tree held still", current);
        }

        // Content decides what changed, which holds whatever the clock did; a date decides whether the
        // build system can see it.
        return (ChangedSinceRecord(request, previous, current, buildDirectory), current);
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
    /// The tracked files whose content decides whether this variant's build directory can be kept,
    /// and which the build's guards watch; or an empty list, and why, when git could not be asked.
    /// </summary>
    /// <remarks>
    /// An empty list fails closed everywhere it goes. The decision has no set to compare a record
    /// against, and starts from clean; the guards have nothing to watch, and the leg is unmeasured; and
    /// the record the build writes as it begins holds no fingerprint, so the next build cannot conclude
    /// the tree held still either, and starts from clean too.
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

        // A tree git tracks nothing in is said, not passed over: nothing is watched while this builds,
        // and the record it leaves holds no fingerprint, so the next build of this variant starts from
        // clean. A copy a sync made was exactly such a tree, until each sync staged what it carried -
        // and every build there rebuilt from clean with no word about why.
        if (tracked.Count == 0)
        {
            _output.Warn(
                CommandName,
                $"{request.Leg}: git tracks no file in '{request.TreeRoot}', so nothing is watched while this builds "
                + "and nothing it records lets the next build keep this directory: nothing here can say the "
                + "tree held still. A host's copy has what each sync carries staged by that sync.");
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
    /// Why an input changed since the previous build began is one its date hides from the build
    /// system - dated no later than the newest file that build left - or <see langword="null"/> where
    /// none is.
    /// </summary>
    /// <remarks>
    /// Which inputs changed is the question no clock can answer wrongly, and it is asked by content,
    /// or, after a build that never finished, by whether the file was written again at all, a question
    /// of equality too. Whether the build system will see one is asked by date, as the build system
    /// asks: one dated after everything the build left is newer than every output that reads it. The
    /// directory of a build that never finished is read for its newest file only where an input
    /// changed.
    /// </remarks>
    private string? ChangedSinceRecord(BuildRequest request, BuildRecord previous, InputSnapshot current, string buildDirectory)
    {
        var finished = previous.Newest is not null;
        var newest = previous.Newest;
        var read = finished;

        foreach (var file in current.Files)
        {
            // One the record never held is new to the set since, and judged as a file outside it is;
            // one that is gone is seen gone by the build system, which never asks its date.
            if (!previous.Contents.TryGetValue(file.Path, out var content) || file.Length == InputFingerprint.AbsentLength)
            {
                continue;
            }

            var differs = !content.Equals(file.Content, StringComparison.Ordinal);

            // Content that held, in a build that finished, is what its guards vouched for.
            if (!differs && (finished || !previous.Written.TryGetValue(file.Path, out _)))
            {
                continue;
            }

            DateTime written;

            try
            {
                written = _fileSystem.LastWriteTimeUtc(Path.Combine(request.TreeRoot, file.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"an undated input: '{file.Path}' changed, or may have, and could not be dated against what "
                    + $"this directory holds: {ex.Message.TrimEnd('.')}";
            }

            if (!differs && written == previous.Written[file.Path])
            {
                continue;
            }

            if (!read)
            {
                var left = Survey(buildDirectory);

                if (left.Unreadable is { } unreadable)
                {
                    // Without the newest date nothing says the build system will see the change, which is
                    // the absence of an answer, and rebuilds for that reason alone.
                    return $"an unreadable build directory: '{file.Path}' changed, and what this directory holds "
                        + $"could not all be read to date it against: {unreadable.TrimEnd('.')}";
                }

                newest = left.Newest;
                read = true;
            }

            // Nothing in the directory, so nothing a change could be dated behind.
            if (newest is null)
            {
                return null;
            }

            if (written <= newest.LastWriteTimeUtc)
            {
                var how = differs
                    ? $"'{file.Path}' differs from what this directory was last given to build"
                    : $"'{file.Path}' was written again after the last build began, which never finished and may "
                        + "have read it in between";
                var where = finished ? "the newest file that build left" : "the newest file in the directory";

                return $"a change a build system could miss: {how}, and is dated {written:u}, no later than "
                    + $"'{newest.Path}' at {newest.LastWriteTimeUtc:u}, {where} - as a stepped clock, or a tool that "
                    + "keeps a file's old time, leaves it";
            }
        }

        return null;
    }

    /// <summary>
    /// What a build records as it begins: every input's content as the build system is given it, when
    /// each had last been written, and - where one could not be read - that nothing it stamps can be
    /// ordered.
    /// </summary>
    /// <param name="request">The leg's build.</param>
    /// <param name="builtFrom">
    /// The inputs as the build began, or <see langword="null"/> when none were watched, which leaves
    /// the next build no record to compare.
    /// </param>
    /// <remarks>
    /// The fingerprint is what makes the next build's staleness decision independent of the clock.
    /// Without it the only question that can be asked is whether a source is newer than an object,
    /// which is exactly the question a stepped clock answers wrongly. Taken as the build began, not
    /// after it: the build system read the tree it was given, and a file edited after the build ended
    /// is a change for the next build to date, not part of what this one built. The times are for a
    /// build that never finishes, whose guards never say whether its tree held still: an input whose
    /// time is not what it was is one written again since. One whose time could not be read is left
    /// out of them, and compared by its content alone.
    /// </remarks>
    private BuildRecord Began(BuildRequest request, InputSnapshot? builtFrom)
    {
        var files = builtFrom?.Files ?? [];
        var written = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var file in files.Where(file => file.Length != InputFingerprint.AbsentLength))
        {
            try
            {
                written[file.Path] = _fileSystem.LastWriteTimeUtc(Path.Combine(request.TreeRoot, file.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left out: this input is compared by its content alone.
            }
        }

        return new BuildRecord(
            Unrecorded(builtFrom),
            request.Variant.DirectoryName,
            Newest: null,
            files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal),
            written);
    }

    /// <summary>Writes <paramref name="record"/> into <paramref name="buildDirectory"/>, over the one there.</summary>
    private void Record(string buildDirectory, BuildRecord record)
        => _fileSystem.WriteAllTextAtomic(Path.Combine(buildDirectory, BuildRecord.FileName), record.Write());

    /// <summary>
    /// Why nothing a build stamped can be ordered because one of <paramref name="phases"/> spanned a
    /// clock step, or <see langword="null"/> where none did.
    /// </summary>
    private static string? Stepped(IReadOnlyList<PhaseResult> phases)
        => phases.FirstOrDefault(phase => phase.ClockStepped) is { } stepped
            ? $"a clock step: the previous build's '{stepped.Phase}' phase spanned one, so nothing it stamped can be ordered"
            : null;

    /// <summary>
    /// Why nothing a finished build stamped can be ordered, or <see langword="null"/> when nothing
    /// doubted it.
    /// </summary>
    /// <param name="phases">The phases it ran.</param>
    /// <param name="seen">What its guards saw.</param>
    /// <param name="builtFrom">The inputs as it began.</param>
    /// <remarks>
    /// A tree that moved under the build is the same problem as a clock that stepped: what this
    /// directory holds was compiled from a tree no fingerprint describes. So is a directory something
    /// else used while it built. Each is said as itself, so the next build names what happened
    /// rather than a clock step that never did.
    /// </remarks>
    private static string? Unordered(IReadOnlyList<PhaseResult> phases, LegGuardReport seen, InputSnapshot? builtFrom)
    {
        if (Stepped(phases) is { } stepped)
        {
            return stepped;
        }

        if (Unrecorded(builtFrom) is { } unrecorded)
        {
            return unrecorded;
        }

        if (seen.Inputs?.Verdict() is { } moved)
        {
            return "a moving tree: the previous build's inputs could not be shown to hold still while it "
                + $"ran, so nothing it compiled can be matched to one tree - {moved.Detail}";
        }

        if (seen.Contention?.Verdict() is { } shared)
        {
            return "a shared build directory: the previous build could not be shown to have had its "
                + $"directory to itself, so nothing in it can be ordered - {shared.Detail}";
        }

        return null;
    }

    /// <summary>
    /// Why a record of <paramref name="builtFrom"/> would leave an input unchecked, or
    /// <see langword="null"/> when every input was read.
    /// </summary>
    /// <param name="builtFrom">The inputs as the build began.</param>
    /// <remarks>
    /// An input that could not be read leaves no line in the record, and a path the next build
    /// cannot find in the record is one its content comparison skips - so the file that was
    /// unreadable here is exactly the file that would go unchecked there. Marked instead, which
    /// costs one clean rebuild and cannot hand anybody a stale object. Asked of the record written
    /// as the build begins as well as the one written as it ends, so a build stopped part way leaves
    /// the same mark.
    /// </remarks>
    private static string? Unrecorded(InputSnapshot? builtFrom)
        => builtFrom?.Unreadable is [var first, ..]
            ? $"an unrecorded input: '{first.Path}' could not be read as the previous build began, so "
                + "nothing recorded what it was built from"
            : null;

    /// <summary>
    /// Why <paramref name="buildDirectory"/>, which holds no record, must start from clean - it holds
    /// files, and nothing says what they were built from - or <see langword="null"/> where it holds none.
    /// </summary>
    /// <remarks>
    /// A build writes its record before anything else it puts in its directory, so files with no record
    /// beside them were left by something no record describes: a clean start stopped part way through its
    /// delete, which can take the record and leave the objects the start was there to discard; a
    /// configure run by hand or by an editor; a cache that restored part of a directory; or an earlier
    /// version, which wrote none for a build that failed and that nothing doubted. Kept, they would be
    /// built on as though the tree they came from were known.
    /// </remarks>
    private string? Unaccounted(string buildDirectory)
    {
        if (!_fileSystem.DirectoryExists(buildDirectory))
        {
            return null;
        }

        try
        {
            return _fileSystem.EnumerateWrittenFiles(buildDirectory).FirstOrDefault() is { } found
                ? $"no record to compare: the directory holds files - '{Path.GetRelativePath(buildDirectory, found.Path).Replace(Path.DirectorySeparatorChar, '/')}' "
                    + "among them - and no record of what they were built from, so nothing here can say the tree held still"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "an unreadable build directory: it holds no record, and whether it holds anything else could not be read: "
                + ex.Message.TrimEnd('.');
        }
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
}
