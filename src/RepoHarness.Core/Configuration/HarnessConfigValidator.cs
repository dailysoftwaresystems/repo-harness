using System.Text.RegularExpressions;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// Checks a loaded configuration for problems that would otherwise surface far from
/// their cause.
/// </summary>
/// <remarks>
/// A leg naming a host that is not declared is not a syntax error, so nothing rejects it
/// at load; it fails much later, in the middle of a run, as a lookup miss with no indication
/// of which config line was wrong. Every problem is collected and reported together, because
/// fixing one error only to be shown the next is a poor way to correct a file.
/// </remarks>
public static class HarnessConfigValidator
{
    /// <summary>Schema versions this build understands.</summary>
    private static readonly int[] SupportedVersions = [1];

    /// <summary>Adapters a project may declare.</summary>
    private static readonly string[] ProjectTypes = ["cmake", "dotnet", "dart"];

    /// <summary>Platform keys a per-platform map or list may use.</summary>
    /// <remarks>
    /// Built from the operating systems a host can be measured as, plus the one key that means all
    /// of them, so a platform added to <see cref="PlatformNames"/> is accepted here without anyone
    /// remembering to add it twice.
    /// </remarks>
    private static readonly string[] PlatformKeys = [PlatformScope.Every, .. PlatformNames.OperatingSystems];

    /// <summary>The phases of a leg, which an emulator may run.</summary>
    private static readonly string[] LegPhases = ["build", "test"];

    /// <summary>Returns every problem found, in the order they would be read.</summary>
    public static IReadOnlyList<string> Validate(HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();

        ValidateDefaults(config, problems);
        ValidateWorktrees(config.Worktrees, problems);
        ValidateAnchors(config.Anchors, problems);
        ValidateProjects(config, problems);
        ValidateToolchains(config, problems);
        ValidateHosts(config.Hosts, problems);
        ValidateEmulators(config, problems);
        ValidateLegs(config, problems);
        ValidateTools(config, problems);
        ValidateToolSearchDirectories(config, problems);
        ValidateRunnersAndExec(config, problems);
        ValidateCommit(config.Commit, problems);
        ValidateSync(config.Sync, problems);
        ValidateContention(config.Contention, problems);
        ValidateSecretItems(config, problems);
        ValidateTiming(config, problems);
        ValidateLineEndings(config.LineEndings, problems);
        ValidateCi(config.Ci, problems);

        return problems;
    }

    /// <summary>Throws when <paramref name="config"/> has any problem.</summary>
    /// <exception cref="ConfigException">The configuration is not usable.</exception>
    public static void ThrowIfInvalid(HarnessConfig config, string path)
    {
        var problems = Validate(config);

        if (problems.Count == 0)
        {
            return;
        }

        var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));
        throw new ConfigException(
            $"'{path}' has {problems.Count} problem(s):{Environment.NewLine}{detail}");
    }

    private static void ValidateDefaults(HarnessConfig config, List<string> problems)
    {
        if (!SupportedVersions.Contains(config.Version))
        {
            problems.Add(
                $"version {config.Version} is not supported by this build "
                + $"(supported: {string.Join(", ", SupportedVersions)})");
        }

        var defaults = config.Defaults;

        RequireAtLeastOne(defaults.BuildCores, "defaults.buildCores", problems);
        RequireAtLeastOne(defaults.TestCores, "defaults.testCores", problems);

        if (defaults.MaxParallelLegs is { } maxParallelLegs)
        {
            RequireAtLeastOne(maxParallelLegs, "defaults.maxParallelLegs", problems);
        }

        if (defaults.MaxParallelLegsTotal is { } maxParallelLegsTotal)
        {
            RequireAtLeastOne(maxParallelLegsTotal, "defaults.maxParallelLegsTotal", problems);
        }

        // A ceiling below the per-machine cap makes the per-machine number a claim nothing can
        // honour: no machine could ever reach it, and a reader comparing the two would be told one
        // thing by the file and shown another by the run.
        if (defaults.MaxParallelLegs is { } perMachine
            && defaults.MaxParallelLegsTotal is { } total
            && total < perMachine)
        {
            problems.Add(
                $"defaults.maxParallelLegsTotal ({total}) is below defaults.maxParallelLegs "
                + $"({perMachine}), so no machine could ever run the per-machine number");
        }

        if (defaults.StallSeconds < 0)
        {
            problems.Add($"defaults.stallSeconds cannot be negative, found {defaults.StallSeconds}");
        }

        var factor = defaults.DurationWarningFactor;
        if (!double.IsFinite(factor) || (factor != 0 && factor < 1))
        {
            problems.Add($"defaults.durationWarningFactor must be 0 (off) or at least 1, found {factor}");
        }

        if (defaults.ClockStepToleranceMilliseconds < 0)
        {
            problems.Add(
                $"defaults.clockStepToleranceMilliseconds cannot be negative, found {defaults.ClockStepToleranceMilliseconds}");
        }

        RequireAtLeastOne(defaults.ProcessSampleSeconds, "defaults.processSampleSeconds", problems);

        if (defaults.Project is { } project && !HasProject(config, project))
        {
            problems.Add($"defaults.project '{project}' is not declared in projects");
        }
    }

    private static void ValidateWorktrees(WorktreeSettings worktrees, List<string> problems)
    {
        RequireAtLeastOne(worktrees.MaxNameLength, "worktrees.maxNameLength", problems);

        // A root outside the checkout would put worktrees where neither the ignore rules nor the
        // path budget reach them, and one that is the checkout itself would make every worktree a
        // sibling of the tree it came from.
        RequireRelativePaths([worktrees.Root], "worktrees.root", problems);

        if (worktrees.Root.Trim() is "" or "." or "./")
        {
            problems.Add("worktrees.root cannot be the repository root itself");
        }

        RequireRelativePaths(worktrees.EvidenceRoots, "worktrees.evidenceRoots", problems);

        // A negative reserve makes the path budget arithmetic always pass, silently
        // disabling the only guard against the Windows path limit.
        if (worktrees.PathBudgetReserve < 0)
        {
            problems.Add(
                "worktrees.pathBudgetReserve cannot be negative: a negative reserve "
                + "disables the path budget instead of relaxing it");
        }

        if (worktrees.PathBudgetMargin < 0)
        {
            problems.Add("worktrees.pathBudgetMargin cannot be negative");
        }

        if (worktrees.PathLimit is { } pathLimit)
        {
            RequireAtLeastOne(pathLimit, "worktrees.pathLimit", problems);
        }
    }

    private static void ValidateAnchors(AnchorSettings anchors, List<string> problems)
    {
        var pendingValid = ValidateRegistryPath(anchors.PendingAnchorsPath, "anchors.pendingAnchorsPath", problems);
        var doneValid = ValidateRegistryPath(anchors.DoneAnchorsPath, "anchors.doneAnchorsPath", problems);

        // Compared as the locator resolves them, and ignoring case on every platform. Two spellings
        // of one file are one registry on Windows and two on Linux, and a file that is both
        // registries would have a closing anchor moved onto itself.
        if (pendingValid
            && doneValid
            && string.Equals(
                AnchorRegistryLocator.Normalize(anchors.PendingAnchorsPath),
                AnchorRegistryLocator.Normalize(anchors.DoneAnchorsPath),
                StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                "anchors.pendingAnchorsPath and anchors.doneAnchorsPath name the same file; closing "
                + "an anchor moves it from one to the other, so they must differ");
        }

        if (!Regex.IsMatch(anchors.IdPrefix, "^[A-Za-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant))
        {
            problems.Add($"anchors.idPrefix '{anchors.IdPrefix}' must be letters and digits, starting with a letter");
        }

        RequireAtLeastOne(anchors.MinimumIdSegments, "anchors.minimumIdSegments", problems);

        RequireRelativePaths(anchors.CitationRoots, "anchors.citationRoots", problems);
    }

    /// <summary>
    /// Checks the directories holding connection data. They are named here and described nowhere:
    /// a name reaches a directory path and an ssh command line, so anything that could leave the
    /// harness directory or be read as an option is refused before it is used.
    /// </summary>
    private static void ValidateSecretItems(HarnessConfig config, List<string> problems)
    {
        CheckItemNames(config.SshItems, "sshItems", problems);
        CheckItemNames(config.WslDistros, "wslDistros", problems);

        // An ssh host reached by a name with no directory behind it has nowhere to find its address,
        // and would fail at connect time with a message about ssh rather than about this file.
        foreach (var host in config.Hosts.Ssh.Keys.Where(host => DeclaredName.In(config.SshItems, host) is null))
        {
            problems.Add(
                $"host ssh '{host}' has no entry in sshItems, so nothing declares where its "
                + "address, user and key are kept");
        }

        foreach (var distro in config.Hosts.Wsl.Keys.Where(distro => DeclaredName.In(config.WslDistros, distro) is null))
        {
            problems.Add($"host wsl '{distro}' has no entry in wslDistros");
        }
    }

    private static void CheckItemNames(List<string> names, string setting, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (!seen.Add(name))
            {
                problems.Add($"{setting} declares '{name}' more than once; names are compared ignoring case");
            }

            CheckHostName(name, setting, problems);
        }
    }

    private static void ValidateTiming(HarnessConfig config, List<string> problems)
    {
        foreach (var (pattern, index) in config.BuildTimingRegex.Select((pattern, index) => (pattern, index)))
        {
            CheckPattern(pattern, $"buildTimingRegex[{index}]", problems);
        }

        foreach (var (pattern, index) in config.TestTimingRegex.Select((pattern, index) => (pattern, index)))
        {
            CheckPattern(pattern, $"testTimingRegex[{index}]", problems);
        }

        foreach (var (pattern, index) in config.RunTimingRegex.Select((pattern, index) => (pattern, index)))
        {
            CheckPattern(pattern, $"runTimingRegex[{index}]", problems);
        }
    }

    private static void ValidateLineEndings(LineEndingSettings lineEndings, List<string> problems)
    {
        RequireRelativePaths(lineEndings.Exclude, "lineEndings.exclude", problems);

        // Read by the same matcher as sync.exclude, so checked by the same rule. Left unchecked, a
        // '*' anywhere but the leading '**/' is compared as text, matches nothing, and the line sits
        // in the file reading as protection.
        CheckPathPatterns(lineEndings.Exclude, "lineEndings.exclude", problems);
    }

    private static void ValidateCi(CiSettings ci, List<string> problems)
    {
        RequireRelativePaths(ci.Workflows, "ci.workflows", problems);

        if (ci.LegBudgetMinutes < 0)
        {
            problems.Add($"ci.legBudgetMinutes cannot be negative, found {ci.LegBudgetMinutes}");
        }
    }

    /// <summary>Checks one registry path, and reports whether it is usable.</summary>
    private static bool ValidateRegistryPath(string path, string setting, List<string> problems)
    {
        var before = problems.Count;
        RequireRelativePaths([path], setting, problems);

        if (problems.Count == before && !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{setting} '{path}' must be a markdown (.md) file");
        }

        return problems.Count == before;
    }

    private static void ValidateProjects(HarnessConfig config, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in config.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.Name))
            {
                problems.Add("a project has no name");
                continue;
            }

            if (!seen.Add(project.Name))
            {
                problems.Add($"project '{project.Name}' is declared more than once");
            }

            if (!ProjectTypes.Contains(project.Type, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"project '{project.Name}' has type '{project.Type}'; "
                    + $"known types are {string.Join(", ", ProjectTypes)}");
            }

            foreach (var format in project.RebuildableFormats)
            {
                // A blank entry in a list that is otherwise a statement. Left alone it matches
                // every file's extension against "." and nothing's name, which is neither what it
                // says nor a safe reading of it.
                if (string.IsNullOrWhiteSpace(format))
                {
                    problems.Add(
                        $"project '{project.Name}' rebuildableFormats has a blank entry; each is an "
                        + "extension or a whole file name, such as '.cpp' or 'CMakeLists.txt'");
                }
                else if (format.Contains('/', StringComparison.Ordinal)
                    || format.Contains('\\', StringComparison.Ordinal)
                    || format.Contains('*', StringComparison.Ordinal))
                {
                    // Said rather than ignored, because a path or a glob here looks like it works
                    // and matches nothing: the entries are kinds of file, not places.
                    problems.Add(
                        $"project '{project.Name}' rebuildableFormats names '{format}', which is a "
                        + "path or a pattern; entries are extensions or whole file names, such as "
                        + "'.cpp' or 'CMakeLists.txt', and apply wherever such a file is tracked");
                }
            }

            foreach (var (platform, toolchain) in project.DefaultToolchain)
            {
                if (!PlatformKeys.Contains(platform, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"project '{project.Name}' defaultToolchain has key '{platform}'; "
                        + $"expected one of {string.Join(", ", PlatformKeys)}");
                }

                if (!config.Toolchains.ContainsKey(toolchain))
                {
                    problems.Add(
                        $"project '{project.Name}' defaultToolchain['{platform}'] names "
                        + $"toolchain '{toolchain}', which is not declared");
                    continue;
                }

                var owner = $"project '{project.Name}' defaultToolchain['{platform}']";

                // A key naming one platform says which platform it is for. The 'all' key does not,
                // so it is checked against the operating systems the legs building this project
                // actually declare: a general default that cannot serve one of them is the same
                // contradiction, reached by a different route.
                if (string.Equals(platform, PlatformScope.Every, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var os in OperatingSystemsBuilding(config, project))
                    {
                        RequireToolchainOn(config, toolchain, os, owner, problems);
                    }
                }
                else
                {
                    RequireToolchainOn(config, toolchain, platform, owner, problems);
                }
            }

            ValidateBuildOutputs(config, project, problems);
            ValidateTest(project.Test, $"project '{project.Name}'", config, problems);
        }
    }

    /// <summary>
    /// Checks a project's build outputs: every path relative, every platform key known, and every
    /// leg that builds the project covered by every entry.
    /// </summary>
    /// <remarks>
    /// The coverage check is what makes a keyed entry safe. An entry naming a path for Windows
    /// alone, in a repository whose legs also run Linux, would leave the Linux legs with one fewer
    /// witness than the file appears to give them — and a witness that quietly is not checked is
    /// the failure the whole <c>buildOutputs</c> mechanism exists to prevent. It is refused here,
    /// naming the leg, because every declared leg and its operating system are known when the file
    /// is read; leaving it to the run would report it once per leg, much later, and only on the
    /// legs that happened to be selected.
    /// </remarks>
    private static void ValidateBuildOutputs(HarnessConfig config, ProjectConfig project, List<string> problems)
    {
        var owner = $"project '{project.Name}' buildOutputs";

        RequireRelativePaths(
            project.BuildOutputs.SelectMany(output => output.Paths.Values),
            owner,
            problems);

        foreach (var platform in project.BuildOutputs.SelectMany(output => output.Paths.Keys))
        {
            if (!PlatformKeys.Contains(platform, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{owner} has an entry for '{platform}'; "
                    + $"expected one of {string.Join(", ", PlatformKeys)}");
            }
        }

        foreach (var output in project.BuildOutputs.Where(output => output.Plain is null))
        {
            foreach (var (leg, os) in LegsBuilding(config, project).Where(leg => !output.Covers(leg.Os)))
            {
                problems.Add(
                    $"{owner} entry '{output}' names no path for '{os}', which leg '{leg}' builds on; "
                    + $"give it a '{os}' entry or an '{PlatformScope.Every}' one");
            }
        }
    }

    /// <summary>
    /// Checks that <paramref name="toolchain"/> is declared to exist on <paramref name="platform"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refused when the file is read rather than skipped when a leg is placed, because nothing here
    /// depends on measuring anything: a leg's <c>os</c> is required, and a leg is only ever placed
    /// on a host whose operating system equals it — emulation varies the processor, never the
    /// system. So the contradiction is entirely between two lines of this file, and a check at
    /// placement could never fire for anything this one has not already caught.
    /// </para>
    /// <para>
    /// Refusing also keeps the answer to "what ran" unchanged. Skipping the leg instead would add a
    /// new reason for a run to report legs that reached no verdict, and such a run is not a
    /// success; a repository whose platform lists were wrong would find previously-green runs
    /// turning non-zero without anything about its code having changed.
    /// </para>
    /// </remarks>
    /// <param name="config">The configuration, for its toolchains.</param>
    /// <param name="toolchain">The toolchain named. Already known to be declared.</param>
    /// <param name="platform">The operating system it would have to exist on.</param>
    /// <param name="owner">What names it, as the problem reports it.</param>
    /// <param name="problems">Collects the problem.</param>
    private static void RequireToolchainOn(
        HarnessConfig config,
        string toolchain,
        string platform,
        string owner,
        List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(platform)
            || !config.Toolchains.TryGetValue(toolchain, out var declared)
            || PlatformScope.Applies(declared.Platforms, platform))
        {
            return;
        }

        problems.Add(
            $"{owner} names toolchain '{toolchain}', which declares platforms "
            + $"{string.Join(", ", declared.Platforms)} and so does not exist on '{platform}'");
    }

    /// <summary>
    /// The legs that build <paramref name="project"/>, each with the operating system it declares.
    /// </summary>
    /// <remarks>
    /// A leg naming another project, or none where several are declared, says nothing about what
    /// this one must produce or build with.
    /// </remarks>
    /// <param name="config">The configuration, for its legs.</param>
    /// <param name="project">The project.</param>
    private static IReadOnlyList<(string Leg, string Os)> LegsBuilding(HarnessConfig config, ProjectConfig project)
        => [.. config.Legs
            .Where(leg => BuildsProject(config, leg.Value, project))
            .Select(leg => (Leg: leg.Key, leg.Value.Os))
            .Where(leg => !string.IsNullOrWhiteSpace(leg.Os))];

    /// <summary>The operating systems the legs building <paramref name="project"/> declare, once each.</summary>
    /// <param name="config">The configuration, for its legs.</param>
    /// <param name="project">The project.</param>
    private static IEnumerable<string> OperatingSystemsBuilding(HarnessConfig config, ProjectConfig project)
        => LegsBuilding(config, project).Select(leg => leg.Os).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="leg"/> builds <paramref name="project"/>, by its own name for it or
    /// by the repository's default.
    /// </summary>
    private static bool BuildsProject(HarnessConfig config, LegConfig leg, ProjectConfig project)
    {
        var named = leg.Project ?? config.Defaults.Project;

        return named is null
            ? config.Projects.Count == 1
            : string.Equals(named, project.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateToolchains(HarnessConfig config, List<string> problems)
    {
        foreach (var (name, toolchain) in config.Toolchains)
        {
            foreach (var platform in toolchain.Platforms)
            {
                if (!PlatformKeys.Contains(platform, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"toolchain '{name}' lists platform '{platform}'; "
                        + $"expected one of {string.Join(", ", PlatformKeys)}");
                }
            }
        }
    }

    private static void ValidateHosts(HostsConfig hosts, List<string> problems)
    {
        ValidateHostSettings(hosts.Local, "hosts.local", problems);

        foreach (var (name, host) in hosts.Wsl)
        {
            var owner = $"hosts.wsl '{name}'";

            CheckHostName(name, owner, problems);
            ValidateHostSettings(host, owner, problems);
            CheckRepositoryPath(host.RepositoryPath, owner, allowWindowsPaths: false, problems);
        }

        foreach (var (name, host) in hosts.Ssh)
        {
            var owner = $"hosts.ssh '{name}'";

            CheckHostName(name, owner, problems);
            ValidateHostSettings(host, owner, problems);
            CheckRepositoryPath(host.RepositoryPath, owner, allowWindowsPaths: true, problems);
            RequireAtLeastOne(host.ConnectTimeoutSeconds, $"{owner} connectTimeoutSeconds", problems);
            RequireAtLeastOne(host.KeepAliveSeconds, $"{owner} keepAliveSeconds", problems);
        }
    }

    private static void ValidateHostSettings(HostSettings host, string owner, List<string> problems)
    {
        if (host.BuildCores is { } buildCores)
        {
            RequireAtLeastOne(buildCores, $"{owner} buildCores", problems);
        }

        if (host.TestCores is { } testCores)
        {
            RequireAtLeastOne(testCores, $"{owner} testCores", problems);
        }

        if (host.KeepAwake is { } keepAwake && IsBlankCommand(keepAwake))
        {
            problems.Add($"{owner} keepAwake has an empty command");
        }
    }

    private static void ValidateEmulators(HarnessConfig config, List<string> problems)
    {
        foreach (var (name, emulator) in config.Emulators)
        {
            var owner = $"emulator '{name}'";

            CheckOs(emulator.HostOs, $"{owner} hostOs", problems);
            CheckProcessor(emulator.HostProcessor, $"{owner} hostProcessor", problems);
            CheckProcessor(emulator.Processor, $"{owner} processor", problems);

            if (SameName(emulator.HostProcessor, emulator.Processor))
            {
                problems.Add(
                    $"{owner} runs {emulator.Processor} programs on {emulator.HostProcessor} hosts, which needs no emulator");
            }

            // An empty launcher would run the witness, and every program after it, as though the
            // host ran them natively: exactly what an emulator entry exists to rule out.
            if (emulator.Launcher is { } launcher)
            {
                if (IsBlankCommand(launcher))
                {
                    problems.Add($"{owner} launcher is empty; leave it out when the operating system runs the programs itself");
                }
                else
                {
                    CheckProgram(launcher[0], $"{owner} launcher", problems);
                }
            }

            if (emulator.Requires.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{owner} requires contains a blank entry");
            }

            foreach (var requirement in emulator.Requires.Where(requirement => !string.IsNullOrWhiteSpace(requirement)))
            {
                CheckProgram(requirement, $"{owner} requires", problems);
            }

            if (emulator.Phases.Count == 0)
            {
                problems.Add($"{owner} phases is empty; list test, build or both");
            }

            foreach (var phase in emulator.Phases.Where(phase => !LegPhases.Contains(phase, StringComparer.OrdinalIgnoreCase)))
            {
                problems.Add($"{owner} phases names '{phase}'; expected one of {string.Join(", ", LegPhases)}");
            }

            if (IsBlankCommand(emulator.Witness.Command))
            {
                problems.Add($"{owner} witness has an empty command");
            }
            else if (emulator.Launcher is not null)
            {
                // Behind a launcher the witness is only an argument. The launcher finds it, not the PATH
                // lookup, and qemu's user mode, for one, opens a bare name in the directory it starts in.
                CheckAbsolute(emulator.Witness.Command[0], $"{owner} witness", problems);
            }
            else
            {
                CheckProgram(emulator.Witness.Command[0], $"{owner} witness", problems);
            }

            CheckPattern(emulator.Witness.Pattern, $"{owner} witness.pattern", problems);

            if (emulator.Tool is { } tool && !config.Tools.Any(declared => SameName(declared.Name, tool)))
            {
                problems.Add($"{owner} names tool '{tool}', which is not declared");
            }
        }
    }

    /// <summary>
    /// Rejects a program, or a required file, named by a relative path. A bare name is looked up on the
    /// PATH of the host it runs on, and an absolute path is used as given. A relative path would resolve
    /// against whichever directory each host starts programs in, which differs from host to host, and
    /// would let a file shipped in the repository stand in for the tool it is named after.
    /// </summary>
    private static void CheckProgram(string program, string setting, List<string> problems)
    {
        var isPath = program.Contains('/', StringComparison.Ordinal) || program.Contains('\\', StringComparison.Ordinal);

        if (isPath && !IsAbsolute(program))
        {
            problems.Add($"{setting} '{program}' must be a program name, looked up on the PATH, or an absolute path");
        }
    }

    /// <summary>Rejects a program that must be named by an absolute path, for the reason its caller gives.</summary>
    private static void CheckAbsolute(string program, string setting, List<string> problems)
    {
        if (!IsAbsolute(program))
        {
            problems.Add($"{setting} '{program}' must be an absolute path: behind a launcher it is found by the launcher, which may not search the PATH");
        }
    }

    private static bool IsAbsolute(string program) => program.StartsWith('/') || IsWindowsAbsolute(program);

    /// <summary>Whether a command names no program: it is empty, or its first word is blank.</summary>
    private static bool IsBlankCommand(IReadOnlyList<string> command) => command.Count == 0 || string.IsNullOrWhiteSpace(command[0]);

    /// <summary>
    /// Rejects a leg or leg set name that <c>--legs</c> could never select. Its value is split at commas,
    /// and the command line at spaces, so a name holding either, or no name at all, is unreachable.
    /// </summary>
    private static void CheckSelectableName(string name, string owner, List<string> problems)
    {
        if (name.Length == 0 || name.Any(character => character == ',' || char.IsWhiteSpace(character)))
        {
            problems.Add($"{owner} cannot be selected with --legs; use a name with no commas or spaces");
        }
    }

    private static void ValidateLegs(HarnessConfig config, List<string> problems)
    {
        foreach (var (name, leg) in config.Legs)
        {
            var owner = $"leg '{name}'";

            CheckSelectableName(name, owner, problems);
            CheckOs(leg.Os, $"{owner} os", problems);
            CheckProcessor(leg.Processor, $"{owner} processor", problems);
            ValidateLegHost(config, owner, leg, problems);
            ValidateLegEmulator(config, owner, leg, problems);

            if (!config.BuildConfigs.ContainsKey(leg.Config))
            {
                problems.Add($"{owner} names config '{leg.Config}', which is not declared");
            }

            if (leg.Toolchain is { } toolchain)
            {
                if (!config.Toolchains.ContainsKey(toolchain))
                {
                    problems.Add($"{owner} names toolchain '{toolchain}', which is not declared");
                }
                else
                {
                    RequireToolchainOn(config, toolchain, leg.Os, owner, problems);
                }
            }

            if (leg.Sanitizer is { } sanitizer && !config.Sanitizers.ContainsKey(sanitizer))
            {
                problems.Add($"{owner} names sanitizer '{sanitizer}', which is not declared");
            }

            if (leg.Project is { } project && !HasProject(config, project))
            {
                problems.Add($"{owner} names project '{project}', which is not declared");
            }

            if (leg.Worktree is { } worktree
                && !WorktreeName.ValidateFormat(worktree).TryGetName(out _, out var worktreeError))
            {
                problems.Add($"{owner} worktree: {worktreeError}");
            }

            ValidateTest(leg.Test, owner, config, problems);
        }

        foreach (var (setName, legs) in config.LegSets)
        {
            CheckSelectableName(setName, $"legSet '{setName}'", problems);

            // --legs accepts both kinds of name, so one that is both would select either depending
            // on which the harness happened to look up first.
            if (config.Legs.ContainsKey(setName))
            {
                problems.Add($"legSet '{setName}' has the same name as a leg, so --legs {setName} would be ambiguous");
            }

            if (legs.Count == 0)
            {
                problems.Add($"legSet '{setName}' is empty");
            }

            foreach (var leg in legs.Where(leg => !config.Legs.ContainsKey(leg)))
            {
                problems.Add($"legSet '{setName}' names leg '{leg}', which is not declared");
            }
        }
    }

    private static void ValidateLegHost(HarnessConfig config, string owner, LegConfig leg, List<string> problems)
    {
        if (leg.Wsl is not null && leg.Ssh is not null)
        {
            problems.Add($"{owner} names both a wsl and an ssh host; a leg runs on one host");
        }

        if (leg.Wsl is { } wsl)
        {
            if (!config.Hosts.Wsl.ContainsKey(wsl))
            {
                problems.Add($"{owner} names wsl host '{wsl}', which is not declared under hosts.wsl");
            }
            else if (!SameName(leg.Os, PlatformNames.Linux))
            {
                problems.Add($"{owner} runs on {leg.Os} but names wsl host '{wsl}', which runs linux");
            }
        }

        if (leg.Ssh is { } ssh && !config.Hosts.Ssh.ContainsKey(ssh))
        {
            problems.Add($"{owner} names ssh host '{ssh}', which is not declared under hosts.ssh");
        }
    }

    private static void ValidateLegEmulator(HarnessConfig config, string owner, LegConfig leg, List<string> problems)
    {
        if (leg.Emulator is not { } emulatorName)
        {
            return;
        }

        if (!config.Emulators.TryGetValue(emulatorName, out var emulator))
        {
            problems.Add($"{owner} names emulator '{emulatorName}', which is not declared");
            return;
        }

        // An emulator runs programs for one processor, without changing the operating system, so
        // a leg it can never serve is a mistake in the file rather than something to measure.
        if (!SameName(emulator.Processor, leg.Processor))
        {
            problems.Add($"{owner} is for {leg.Processor}, but emulator '{emulatorName}' runs {emulator.Processor} programs");
        }

        if (!SameName(emulator.HostOs, leg.Os))
        {
            problems.Add($"{owner} runs on {leg.Os}, but emulator '{emulatorName}' runs on {emulator.HostOs} hosts");
        }
    }

    /// <summary>
    /// Refuses a <c>toolSearchDirectories</c> entry naming a platform that does not exist, and a
    /// directory that is blank or relative.
    /// </summary>
    /// <remarks>
    /// A relative directory would be looked in relative to wherever the command happened to start,
    /// so one leg would find a tool and the next, started elsewhere, would not. <c>~/</c> is allowed
    /// and means the home directory of whoever searches, on each host its own.
    /// </remarks>
    private static void ValidateToolSearchDirectories(HarnessConfig config, List<string> problems)
    {
        foreach (var (platform, directories) in config.ToolSearchDirectories)
        {
            if (!PlatformKeys.Contains(platform, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"toolSearchDirectories names platform '{platform}'; "
                    + $"expected one of {string.Join(", ", PlatformKeys)}");
            }

            foreach (var directory in directories ?? [])
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    problems.Add($"toolSearchDirectories.{platform} has a blank entry");
                    continue;
                }

                var rooted = directory.StartsWith("~/", StringComparison.Ordinal)
                    || directory.StartsWith('/')
                    || Path.IsPathRooted(directory)
                    || (directory.Length >= 3 && directory[1] == ':' && directory[2] is '/' or '\\');

                if (!rooted)
                {
                    problems.Add(
                        $"toolSearchDirectories.{platform} lists '{directory}', which is relative; name the "
                        + "directory absolutely, or from the home directory as '~/...', so every command "
                        + "looks in the same place wherever it started");
                }
            }
        }
    }

    private static void ValidateTools(HarnessConfig config, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var legPlatforms = config.Legs.Values
            .Select(leg => leg.Os)
            .Where(os => !string.IsNullOrWhiteSpace(os))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var tool in config.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                problems.Add("a tool has no name");
                continue;
            }

            if (!seen.Add(tool.Name))
            {
                problems.Add($"tool '{tool.Name}' is declared more than once");
            }

            CheckPattern(tool.Probe?.Regex, $"tool '{tool.Name}' probe.regex", problems);

            foreach (var platform in tool.Platforms)
            {
                if (!PlatformKeys.Contains(platform, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"tool '{tool.Name}' lists platform '{platform}'; "
                        + $"expected one of {string.Join(", ", PlatformKeys)}");
                }
            }

            // A list naming only platforms no leg runs on removes the tool from every host, which
            // reads exactly like never having declared it. Refused for the reason a buildOutput
            // covering no leg is: a rule that applies nowhere is one the file appears to state and
            // nothing enforces, and the spelling that causes it is a single mistyped word.
            if (tool.Platforms.Count > 0
                && legPlatforms.Count > 0
                && !legPlatforms.Any(os => PlatformScope.Applies(tool.Platforms, os)))
            {
                problems.Add(
                    $"tool '{tool.Name}' is needed only on {string.Join(", ", tool.Platforms)}, "
                    + $"and no declared leg runs on any of those ({string.Join(", ", legPlatforms)}), "
                    + "so it would never be checked anywhere");
            }

            if (tool.MinVersion is { } minVersion && !Version.TryParse(minVersion, out _))
            {
                problems.Add($"tool '{tool.Name}' minVersion '{minVersion}' is not a version");
            }

            foreach (var (platform, install) in tool.Install)
            {
                if (!PlatformKeys.Contains(platform, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"tool '{tool.Name}' has an install entry for '{platform}'; "
                        + $"expected one of {string.Join(", ", PlatformKeys)}");
                }

                // An entry with neither a package manager nor a command is a step that
                // silently does nothing when the tool turns out to be missing.
                if (string.IsNullOrWhiteSpace(install.Manager) && (install.Command?.Count ?? 0) == 0)
                {
                    problems.Add(
                        $"tool '{tool.Name}' install['{platform}'] declares neither a manager nor a command");
                }

                if (!string.IsNullOrWhiteSpace(install.Manager) && string.IsNullOrWhiteSpace(install.Id))
                {
                    problems.Add(
                        $"tool '{tool.Name}' install['{platform}'] names manager "
                        + $"'{install.Manager}' but no package id");
                }
            }
        }
    }

    private static void ValidateRunnersAndExec(HarnessConfig config, List<string> problems)
    {
        foreach (var (name, runner) in config.PredefinedRunners)
        {
            foreach (var leg in runner.Legs.Where(leg => !config.Legs.ContainsKey(leg)))
            {
                problems.Add($"predefined runner '{name}' names leg '{leg}', which is not declared");
            }

            // Two descriptions of what one runner does would eventually disagree, and nothing could
            // say which of them ran.
            var hasAction = !string.IsNullOrWhiteSpace(runner.Action);

            if (hasAction && runner.Phases.Count > 0)
            {
                problems.Add($"predefined runner '{name}' declares both an action file and phases; it takes one or the other");
            }
            else if (!hasAction && runner.Phases.Count == 0)
            {
                problems.Add($"predefined runner '{name}' declares neither an action file nor phases");
            }

            // The same rule the parser applies when it reads the file, so that a configuration
            // 'legs' calls valid is one 'run' can act on. Applied here it is a problem listed with
            // every other; left only to the parser it surfaced when a runner was finally invoked,
            // which is exactly the late failure this file exists to prevent.
            if (hasAction && ActionPath.Problem(runner.Action) is { } actionProblem)
            {
                problems.Add($"predefined runner '{name}' action {actionProblem}");
            }

            if (runner.StallSeconds is { } runnerStall && runnerStall < 0)
            {
                problems.Add($"predefined runner '{name}' has a negative stallSeconds");
            }

            ValidateExpectedExceptions(config, name, runner, problems);

            foreach (var phase in runner.Phases)
            {
                // `required` guarantees the property is present, never that it holds
                // anything: an empty command fails later as an empty file name.
                if (IsBlankCommand(phase.Command))
                {
                    problems.Add($"predefined runner '{name}' phase '{phase.Name}' has an empty command");
                }

                if (phase.StallSeconds is { } stall && stall < 0)
                {
                    problems.Add($"predefined runner '{name}' phase '{phase.Name}' has a negative stallSeconds");
                }

                CheckPattern(phase.SuccessPattern, $"predefined runner '{name}' phase '{phase.Name}' successPattern", problems);
            }
        }

        foreach (var (name, _) in config.Exec.Where(entry => string.IsNullOrWhiteSpace(entry.Value.Command)))
        {
            problems.Add($"exec '{name}' has an empty command");
        }
    }

    /// <summary>
    /// Checks every excused failure a runner declares, and the checks that gate it.
    /// </summary>
    /// <remarks>
    /// An excusal that nobody can audit stops being a record of a measured confound and becomes a
    /// way to make a regression invisible, so an entry that does not show its work is refused rather
    /// than trusted. Every measurement is biased toward ABSENT: where this cannot establish that an
    /// entry is scoped and earned, it refuses the entry instead of giving it the benefit of the doubt.
    /// </remarks>
    private static void ValidateExpectedExceptions(
        HarnessConfig config,
        string name,
        RunnerConfig runner,
        List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (expected, index) in runner.ExpectedExceptions.Select((entry, index) => (entry, index)))
        {
            var owner = $"predefined runner '{name}' expectedExceptions[{index}]";

            RequireText(expected.ExceptionType, $"{owner} exceptionType", problems);
            RequireText(expected.Message, $"{owner} message", problems);
            RequireText(expected.EarnedOn, $"{owner} earnedOn", problems);
            RequireText(expected.EarnedAt, $"{owner} earnedAt", problems);
            RequireText(expected.Mechanism, $"{owner} mechanism", problems);
            RequireText(expected.Anchor, $"{owner} anchor", problems);

            if (!string.IsNullOrWhiteSpace(expected.EarnedOn)
                && !DateOnly.TryParseExact(expected.EarnedOn, "yyyy-MM-dd", out _))
            {
                problems.Add($"{owner} earnedOn '{expected.EarnedOn}' is not a date spelled yyyy-MM-dd");
            }

            if (expected.ResultCode < 0)
            {
                problems.Add($"{owner} has a negative resultCode, which no process reports");
            }

            // An entry matching nothing in particular excuses whatever happens to fail, which is the
            // unconditional claim this lint exists to refuse: it hides the regression it was written
            // to explain. An empty list and a pattern that matches the empty string are the same
            // claim written two ways.
            if (expected.Messages.Count == 0)
            {
                problems.Add(
                    $"{owner} names no messages, so it would excuse every failure of that exception type; "
                    + "name the wording it was measured against");
            }

            foreach (var (message, messageIndex) in expected.Messages.Select((message, i) => (message, i)))
            {
                var pattern = $"{owner} messages[{messageIndex}]";
                RequireText(message, pattern, problems);

                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                if (!TryCompile(message, pattern, problems, out var regex))
                {
                    continue;
                }

                if (regex.IsMatch(string.Empty))
                {
                    problems.Add($"{pattern} '{message}' matches any message, which is an unconditional excusal");
                }
            }

            // Two entries matching the same thing make which outcome is reported depend on their
            // order in the file, which nothing else in this schema depends on.
            var identity = CompositeKey.Of([expected.ExceptionType, .. expected.Messages]);

            if (!seen.Add(identity))
            {
                problems.Add($"{owner} repeats an earlier entry's exceptionType and messages");
            }

            foreach (var (check, checkIndex) in expected.RunChecks.Select((check, i) => (check, i)))
            {
                ValidateRunCheck(config, name, $"{owner} runChecks[{checkIndex}]", check, problems);
            }
        }
    }

    private static void ValidateRunCheck(
        HarnessConfig config,
        string runnerName,
        string owner,
        RunCheck check,
        List<string> problems)
    {
        var target = DeclaredName.In(config.PredefinedRunners.Keys, check.PredefinedRunner);

        if (target is null)
        {
            problems.Add($"{owner} names predefined runner '{check.PredefinedRunner}', which is not declared");
        }
        else if (SameName(target, runnerName))
        {
            problems.Add($"{owner} names the runner that carries it, so the check would confirm itself");
        }
        else if (config.PredefinedRunners[target].ExpectedExceptions.Any(entry => entry.RunChecks.Count > 0))
        {
            // One level deep, so a check can never recurse and a confirmation can never be
            // confirmed by the thing it confirms.
            problems.Add(
                $"{owner} names predefined runner '{target}', which carries run checks of its own; "
                + "a check is one level deep");
        }

        if (check.MinStepsInFailureWindow < 0)
        {
            problems.Add($"{owner} has a negative minStepsInFailureWindow");
        }

        if (!double.IsFinite(check.MinStepSeconds) || check.MinStepSeconds < 0)
        {
            problems.Add($"{owner} minStepSeconds must be zero or more, found {check.MinStepSeconds}");
        }

        if (check.MinStepsInFailureWindow > 0 && check.MinStepSeconds <= 0)
        {
            problems.Add(
                $"{owner} counts steps in the failure window but gives no minStepSeconds, so every "
                + "sample would count as a step");
        }

        if (check.Expects.ResultCode is { } resultCode && resultCode < 0)
        {
            problems.Add($"{owner} expects a negative resultCode, which no process reports");
        }
    }

    private static void RequireText(string? value, string setting, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{setting} is blank");
        }
    }

    /// <summary>
    /// Checks that the commit template and its variables agree. A placeholder nobody declared is
    /// written into a message literally, and a variable the template never uses is a value asked
    /// for and then dropped; both are found here instead of in a commit someone has already pushed.
    /// </summary>
    private static void ValidateCommit(CommitConfig commit, List<string> problems)
    {
        if (commit.Template is { } blank && string.IsNullOrWhiteSpace(blank))
        {
            problems.Add("commit.template is blank; leave it out to write messages by hand");
        }

        foreach (var (name, variable) in commit.Variables)
        {
            if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant))
            {
                problems.Add(
                    $"commit.variables '{name}' must be letters, digits, hyphens and underscores, starting with a letter");
            }

            // A default means the value is never missing, so "required" would promise a check that
            // can never fire.
            if (variable.Required && variable.Default is not null)
            {
                problems.Add(
                    $"commit.variables '{name}' is required and has a default, so it can never be missing; drop one of them");
            }
        }

        if (commit.Template is not { } template)
        {
            if (commit.Variables.Count > 0)
            {
                problems.Add("commit.variables are declared but there is no commit.template to use them");
            }

            return;
        }

        var used = Regex.Matches(template, @"\{(?<name>[^{}]*)\}", RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var placeholder in used.Where(placeholder => !commit.Variables.ContainsKey(placeholder)))
        {
            problems.Add($"commit.template uses {{{placeholder}}}, which is not declared in commit.variables");
        }

        foreach (var name in commit.Variables.Keys.Where(name => !used.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            problems.Add($"commit.variables '{name}' is never used by commit.template");
        }
    }

    /// <summary>
    /// Checks the paths sync is told about. Each is resolved inside the tree being mirrored, so one
    /// that is absolute or climbs out with ".." would name something that is not part of it.
    /// </summary>
    private static void ValidateSync(SyncConfig sync, List<string> problems)
    {
        RequireRelativePaths(sync.Exclude, "sync.exclude", problems);
        RequireRelativePaths(sync.NeverTransfer, "sync.neverTransfer", problems);
        CheckPathPatterns(sync.NeverTransfer, "sync.neverTransfer", problems);
        CheckPathPatterns(sync.Exclude, "sync.exclude", problems);

        if (!double.IsFinite(sync.MaxDeleteFraction) || sync.MaxDeleteFraction is < 0 or > 1)
        {
            problems.Add(
                $"sync.maxDeleteFraction must be between 0 (no bound) and 1, found {sync.MaxDeleteFraction}");
        }
    }

    /// <summary>
    /// Compiles <paramref name="pattern"/>, reporting the failure rather than throwing, so a caller
    /// can go on to ask what the compiled pattern matches.
    /// </summary>
    private static bool TryCompile(string pattern, string setting, List<string> problems, out Regex regex)
    {
        try
        {
            regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException ex)
        {
            problems.Add($"{setting} is not a valid regular expression: {ex.Message}");
            regex = null!;
            return false;
        }
    }

    /// <summary>Checks the parts of a test section that can be wrong without being malformed.</summary>
    private static void ValidateTest(TestConfig? test, string owner, HarnessConfig config, List<string> problems)
    {
        if (test is null)
        {
            return;
        }

        if (test.All is null && test.Windows is null && test.Linux is null && test.Macos is null)
        {
            problems.Add($"{owner} test declares no invocation; add 'all' or a platform section");
        }

        foreach (var configName in test.Configs.Where(configName => !config.BuildConfigs.ContainsKey(configName)))
        {
            problems.Add($"{owner} test names config '{configName}', which is not declared");
        }

        RequireRelativePaths(test.Inputs, $"{owner} test.inputs", problems);

        (string Section, TestInvocation? Invocation)[] sections =
        [
            ("all", test.All),
            ("windows", test.Windows),
            ("linux", test.Linux),
            ("macos", test.Macos),
        ];

        foreach (var (section, invocation) in sections)
        {
            if (invocation is null)
            {
                continue;
            }

            var setting = $"{owner} test.{section}";

            if (invocation.Cores is { } cores)
            {
                RequireAtLeastOne(cores, $"{setting}.cores", problems);
            }

            CheckPattern(invocation.SuccessPattern, $"{setting}.successPattern", problems);
            CheckCountPattern(invocation.CountPattern, $"{setting}.countPattern", problems);

            if (invocation.CoresArgs is { Count: > 0 } coresArgs
                && !coresArgs.Any(argument => argument.Contains(CoreCounts.Placeholder, StringComparison.Ordinal)))
            {
                problems.Add(
                    $"{setting}.coresArgs never uses {CoreCounts.Placeholder}, so the core count never "
                    + "reaches the runner");
            }

            if (invocation.CoresEnv is { } coresEnv && coresEnv.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{setting}.coresEnv contains a blank variable name");
            }

            // Here rather than when the leg runs. A name nothing fills in reaches the runner as the
            // literal text it was written as, and what a runner makes of a directory that cannot
            // exist is its own business: ctest reports no tests and exits 8, which reads as a suite
            // that ran and found nothing. Found here it names the line to fix, and has cost nobody
            // the build that would have preceded it.
            CheckPlaceholders(invocation.Args, $"{setting}.args", problems);
            CheckPlaceholders(
                invocation.WorkingDirectory is null ? null : [invocation.WorkingDirectory],
                $"{setting}.workingDirectory",
                problems);

            // The one setting where the core count's own name belongs. It is spliced by the rule that
            // owns it and only into these, so the same name in 'args' reaches the runner as literal
            // text: checked here rather than left to disagree with what expands them.
            CheckPlaceholders(
                invocation.CoresArgs,
                $"{setting}.coresArgs",
                problems,
                [CoreCounts.PlaceholderName]);
        }

        // What runs on a platform is its own section merged over 'all', field by field. Every
        // platform that ends up with an invocation must know what to run and how to prove it
        // ran: a zero exit code alone has been measured, more than once, to mean nothing ran.
        (string Platform, TestInvocation? Specific)[] platforms =
        [
            ("windows", test.Windows),
            ("linux", test.Linux),
            ("macos", test.Macos),
        ];

        var applicable = platforms.Where(entry => entry.Specific is not null || test.All is not null).ToList();

        var withoutRunner = applicable
            .Where(entry => string.IsNullOrWhiteSpace(entry.Specific?.Runner ?? test.All?.Runner))
            .Select(entry => entry.Platform)
            .ToList();

        var withoutWitness = applicable
            .Where(entry => (entry.Specific?.SuccessPattern ?? test.All?.SuccessPattern) is null)
            .Select(entry => entry.Platform)
            .ToList();

        if (withoutRunner.Count > 0)
        {
            problems.Add($"{owner} test names no runner for {string.Join(", ", withoutRunner)}");
        }

        if (withoutWitness.Count > 0)
        {
            problems.Add(
                $"{owner} test has no successPattern for {string.Join(", ", withoutWitness)}; "
                + "without one, a runner that exits 0 having run nothing would pass");
        }
    }

    private static void ValidateContention(ContentionConfig contention, List<string> problems)
    {
        if (contention.BuildTools.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("contention.buildTools contains a blank name");
        }

        if (contention.SharedResourceTools.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("contention.sharedResourceTools contains a blank name");
        }

        // A description of a tool nothing watches for reads as protection that does not exist, and
        // it is what a renamed or misspelt entry in sharedResourceTools leaves behind.
        foreach (var (tool, state) in contention.SharedState)
        {
            if (!contention.SharedResourceTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"contention.sharedState describes '{tool}', which contention.sharedResourceTools does not "
                    + "list, so nothing is ever found for it to describe");
            }

            if (string.IsNullOrWhiteSpace(state))
            {
                problems.Add($"contention.sharedState.{tool} is blank; say what the tool shares, or leave it out");
            }
        }
    }

    /// <summary>
    /// Rejects an operating system the harness cannot measure a host as. A misspelt one would
    /// otherwise never match any host, and the leg would be reported unavailable forever instead
    /// of wrong once.
    /// </summary>
    private static void CheckOs(string value, string setting, List<string> problems)
    {
        if (!PlatformNames.OperatingSystems.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{setting} is '{value}'; expected one of {string.Join(", ", PlatformNames.OperatingSystems)}");
        }
    }

    /// <summary>Rejects a processor the harness cannot measure a host as, for the reason <see cref="CheckOs"/> gives.</summary>
    private static void CheckProcessor(string value, string setting, List<string> problems)
    {
        if (!PlatformNames.Processors.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{setting} is '{value}'; expected one of {string.Join(", ", PlatformNames.Processors)}");
        }
    }

    /// <summary>
    /// Rejects a host name that could be read as something other than a name. Names reach the ssh
    /// and wsl.exe command lines, so they hold only letters, digits, dots, hyphens and underscores,
    /// and cannot start with a hyphen, which either program would take for an option.
    /// </summary>
    private static void CheckHostName(string name, string owner, List<string> problems)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
        {
            problems.Add(
                $"{owner} is not a usable name: use letters, digits, dots, hyphens and underscores, "
                + "starting with a letter, a digit or an underscore");
        }
    }

    /// <summary>
    /// Checks where a host keeps its copy of the repository. It is named absolutely, or from the
    /// home directory of the user the host is reached as: a relative path would resolve against
    /// whatever directory a transport happens to start in. A home or root directory itself is
    /// refused, because the copy is kept identical to the repository, and everything else in the
    /// directory would have to go.
    /// </summary>
    private static void CheckRepositoryPath(string path, string owner, bool allowWindowsPaths, List<string> problems)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var isHome = trimmed == "~";
        var isRoot = trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':');

        var isAbsolute = path.StartsWith('/')
            || path.StartsWith("~/", StringComparison.Ordinal)
            || (allowWindowsPaths && IsWindowsAbsolute(path));

        if (string.IsNullOrWhiteSpace(path) || (!isAbsolute && !isHome))
        {
            problems.Add(
                $"{owner} repositoryPath '{path}' must be absolute, or start with ~/ for the home directory"
                + (allowWindowsPaths ? string.Empty : " inside the distribution"));
        }
        else if (path.Split('/', '\\').Any(segment => segment is "." or ".."))
        {
            // Compared as written, a path that steps back up, such as ~/src/.., could still name the home
            // or root directory the next check refuses.
            problems.Add($"{owner} repositoryPath '{path}' has a '.' or '..' segment; name the directory directly");
        }
        else if (isHome || isRoot)
        {
            problems.Add($"{owner} repositoryPath '{path}' is a home or root directory; name a directory of its own for the copy");
        }
    }

    private static bool IsWindowsAbsolute(string path)
        => (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
            || path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// Rejects a path that is absolute or climbs out with "..". These paths are resolved
    /// against a leg's own tree or build directory, and one that escapes it would read, or
    /// demand, something that belongs to another leg. Decided the same way on every platform,
    /// so a file valid on one machine is valid on all of them.
    /// </summary>
    private static void RequireRelativePaths(IEnumerable<string> paths, string setting, List<string> problems)
    {
        foreach (var path in paths)
        {
            var escapes = string.IsNullOrWhiteSpace(path)
                || path[0] is '/' or '\\'
                || (path.Length >= 2 && path[1] == ':')
                || path.Split('/', '\\').Any(segment => segment == "..");

            if (escapes)
            {
                problems.Add($"{setting} entry '{path}' must be a relative path inside the tree, without '..'");
            }
        }
    }

    /// <summary>Checks a pattern that must compile and capture a named group <c>total</c>.</summary>
    private static void CheckCountPattern(string? pattern, string setting, List<string> problems)
    {
        if (pattern is null)
        {
            return;
        }

        try
        {
            var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));

            if (!regex.GetGroupNames().Contains("total", StringComparer.Ordinal))
            {
                problems.Add($"{setting} has no named group 'total' to capture how many tests ran");
            }
        }
        catch (ArgumentException ex)
        {
            problems.Add($"{setting} is not a valid regular expression: {ex.Message}");
        }
    }

    private static bool HasProject(HarnessConfig config, string name)
        => config.Projects.Any(project => SameName(project.Name, name));

    /// <summary>Names in the file compare ignoring case, as its keys do.</summary>
    private static bool SameName(string? first, string? second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static void RequireAtLeastOne(int value, string setting, List<string> problems)
    {
        if (value < 1)
        {
            problems.Add($"{setting} must be at least 1, found {value}");
        }
    }

    /// <summary>
    /// Records a problem for every configured string naming a directory this tool cannot fill in.
    /// </summary>
    /// <param name="values">The configured strings, or null when the setting is absent.</param>
    /// <param name="setting">What to call the setting in the problem.</param>
    /// <param name="problems">Where problems are collected.</param>
    /// <param name="extra">
    /// Names that are legal in this setting beyond the leg's directories, such as the core count's
    /// own name in <c>coresArgs</c>.
    /// </param>
    private static void CheckPlaceholders(
        IReadOnlyList<string>? values,
        string setting,
        List<string> problems,
        IReadOnlyCollection<string>? extra = null)
    {
        foreach (var value in values ?? [])
        {
            try
            {
                LegPathNames.RefuseUnknown(value, setting, PlaceholderPolicy.Refuse, extra);
            }
            catch (HarnessException ex)
            {
                // Collected rather than thrown, so one read of the configuration reports every
                // problem it has rather than the first.
                problems.Add(ex.Message);
            }
        }
    }

    /// <summary>
    /// Records a problem for every sync entry that would match nothing it looks like it matches.
    /// </summary>
    /// <param name="patterns">The entries, as written.</param>
    /// <param name="setting">What to call the setting in the problem.</param>
    /// <param name="problems">Where problems are collected.</param>
    private static void CheckPathPatterns(IReadOnlyList<string>? patterns, string setting, List<string> problems)
    {
        foreach (var pattern in patterns ?? [])
        {
            if (Repository.PathPatterns.Problem(pattern) is { } problem)
            {
                problems.Add($"{setting} names '{pattern}', which {problem}");
            }
        }
    }

    /// <summary>
    /// Rejects a pattern that does not compile, or that is empty. Found at load, a typo in a
    /// success pattern is a clear message; found in the middle of a run, it is a crash in a
    /// phase that had nothing to do with it. An empty pattern matches any output at all, so as
    /// a witness it proves nothing while appearing to prove something.
    /// </summary>
    private static void CheckPattern(string? pattern, string setting, List<string> problems)
    {
        if (pattern is null)
        {
            return;
        }

        if (pattern.Length == 0)
        {
            problems.Add($"{setting} is empty; an empty pattern matches any output, so it proves nothing");
            return;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            problems.Add($"{setting} is not a valid regular expression: {ex.Message}");
        }
    }
}
