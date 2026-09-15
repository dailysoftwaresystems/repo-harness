using System.Text.RegularExpressions;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Platform;
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

    /// <summary>Platform keys a per-platform map may use.</summary>
    private static readonly string[] PlatformKeys = ["all", PlatformNames.Windows, PlatformNames.Linux, PlatformNames.MacOs];

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
        ValidateRunnersAndExec(config, problems);
        ValidateCommit(config.Commit, problems);
        ValidateSync(config.Sync, problems);
        ValidateContention(config.Contention, problems);

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
                }
            }

            RequireRelativePaths(project.BuildOutputs, $"project '{project.Name}' buildOutputs", problems);
            ValidateTest(project.Test, $"project '{project.Name}'", config, problems);
        }
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

        if (host.KeepAwake is { } keepAwake && (keepAwake.Count == 0 || string.IsNullOrWhiteSpace(keepAwake[0])))
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
                if (launcher.Count == 0 || string.IsNullOrWhiteSpace(launcher[0]))
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

            if (emulator.Witness.Command.Count == 0 || string.IsNullOrWhiteSpace(emulator.Witness.Command[0]))
            {
                problems.Add($"{owner} witness has an empty command");
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

        if (isPath && !program.StartsWith('/') && !IsWindowsAbsolute(program))
        {
            problems.Add($"{setting} '{program}' must be a program name, looked up on the PATH, or an absolute path");
        }
    }

    private static void ValidateLegs(HarnessConfig config, List<string> problems)
    {
        foreach (var (name, leg) in config.Legs)
        {
            var owner = $"leg '{name}'";

            CheckOs(leg.Os, $"{owner} os", problems);
            CheckProcessor(leg.Processor, $"{owner} processor", problems);
            ValidateLegHost(config, owner, leg, problems);
            ValidateLegEmulator(config, owner, leg, problems);

            if (!config.BuildConfigs.ContainsKey(leg.Config))
            {
                problems.Add($"{owner} names config '{leg.Config}', which is not declared");
            }

            if (leg.Toolchain is { } toolchain && !config.Toolchains.ContainsKey(toolchain))
            {
                problems.Add($"{owner} names toolchain '{toolchain}', which is not declared");
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

    private static void ValidateTools(HarnessConfig config, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        foreach (var (name, runner) in config.Runners)
        {
            foreach (var leg in runner.Legs.Where(leg => !config.Legs.ContainsKey(leg)))
            {
                problems.Add($"runner '{name}' names leg '{leg}', which is not declared");
            }

            if (runner.Phases.Count == 0)
            {
                problems.Add($"runner '{name}' declares no phases");
            }

            foreach (var phase in runner.Phases)
            {
                // `required` guarantees the property is present, never that it holds
                // anything: an empty command fails later as an empty file name.
                if (phase.Command.Count == 0 || string.IsNullOrWhiteSpace(phase.Command[0]))
                {
                    problems.Add($"runner '{name}' phase '{phase.Name}' has an empty command");
                }

                if (phase.StallSeconds is { } stall && stall < 0)
                {
                    problems.Add($"runner '{name}' phase '{phase.Name}' has a negative stallSeconds");
                }

                CheckPattern(phase.SuccessPattern, $"runner '{name}' phase '{phase.Name}' successPattern", problems);
            }
        }

        foreach (var (name, _) in config.Exec.Where(entry => string.IsNullOrWhiteSpace(entry.Value.Command)))
        {
            problems.Add($"exec '{name}' has an empty command");
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
                && !coresArgs.Any(argument => argument.Contains("{cores}", StringComparison.Ordinal)))
            {
                problems.Add($"{setting}.coresArgs never uses {{cores}}, so the core count never reaches the runner");
            }

            if (invocation.CoresEnv is { } coresEnv && coresEnv.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{setting}.coresEnv contains a blank variable name");
            }
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
