using System.Text.RegularExpressions;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// Checks a loaded configuration for problems that would otherwise surface far from
/// their cause.
/// </summary>
/// <remarks>
/// A leg naming a target that does not exist is not a syntax error, so nothing
/// rejects it at load; it fails much later, in the middle of a run, as a lookup
/// miss with no indication of which config line was wrong. Every problem is
/// collected and reported together, because fixing one error only to be shown the
/// next is a poor way to correct a file.
/// </remarks>
public static class HarnessConfigValidator
{
    /// <summary>Schema versions this build understands.</summary>
    private static readonly int[] SupportedVersions = [1];

    /// <summary>Adapters a project may declare.</summary>
    private static readonly string[] ProjectTypes = ["cmake", "dotnet", "dart"];

    /// <summary>Ways a target can be reached.</summary>
    private static readonly string[] Transports = ["local", "wsl", "ssh"];

    /// <summary>Platform keys a per-platform map may use.</summary>
    private static readonly string[] PlatformKeys = ["all", "windows", "linux", "macos"];

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
        ValidateTargets(config, problems);
        ValidateLegs(config, problems);
        ValidateTools(config, problems);
        ValidateRunnersAndExec(config, problems);
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

        if (defaults.LegSet is { } legSet && !config.LegSets.ContainsKey(legSet))
        {
            problems.Add($"defaults.legSet '{legSet}' is not declared in legSets");
        }

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

    private static void ValidateTargets(HarnessConfig config, List<string> problems)
    {
        foreach (var (name, target) in config.Targets)
        {
            if (target.BuildCores is { } buildCores)
            {
                RequireAtLeastOne(buildCores, $"target '{name}' buildCores", problems);
            }

            if (target.TestCores is { } testCores)
            {
                RequireAtLeastOne(testCores, $"target '{name}' testCores", problems);
            }

            if (target.KeepAwake is { } keepAwake && (keepAwake.Count == 0 || string.IsNullOrWhiteSpace(keepAwake[0])))
            {
                problems.Add($"target '{name}' keepAwake has an empty command");
            }

            RequireAtLeastOne(target.ConnectTimeoutSeconds, $"target '{name}' connectTimeoutSeconds", problems);
            RequireAtLeastOne(target.KeepAliveSeconds, $"target '{name}' keepAliveSeconds", problems);

            if (!Transports.Contains(target.Transport, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"target '{name}' has transport '{target.Transport}'; "
                    + $"known transports are {string.Join(", ", Transports)}");
                continue;
            }

            if (string.Equals(target.Transport, "wsl", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(target.Distro))
            {
                problems.Add($"target '{name}' uses the wsl transport but declares no distro");
            }

            if (string.Equals(target.Transport, "ssh", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(target.RepositoryPath))
            {
                problems.Add($"target '{name}' uses the ssh transport but declares no repositoryPath");
            }
        }
    }

    private static void ValidateLegs(HarnessConfig config, List<string> problems)
    {
        foreach (var (name, leg) in config.Legs)
        {
            if (!config.Targets.ContainsKey(leg.Target))
            {
                problems.Add($"leg '{name}' names target '{leg.Target}', which is not declared");
            }

            if (!config.BuildConfigs.ContainsKey(leg.Config))
            {
                problems.Add($"leg '{name}' names config '{leg.Config}', which is not declared");
            }

            if (leg.Toolchain is { } toolchain && !config.Toolchains.ContainsKey(toolchain))
            {
                problems.Add($"leg '{name}' names toolchain '{toolchain}', which is not declared");
            }

            if (leg.Sanitizer is { } sanitizer && !config.Sanitizers.ContainsKey(sanitizer))
            {
                problems.Add($"leg '{name}' names sanitizer '{sanitizer}', which is not declared");
            }

            if (leg.Project is { } project && !HasProject(config, project))
            {
                problems.Add($"leg '{name}' names project '{project}', which is not declared");
            }

            if (leg.Worktree is { } worktree
                && !WorktreeName.ValidateFormat(worktree).TryGetName(out _, out var worktreeError))
            {
                problems.Add($"leg '{name}' worktree: {worktreeError}");
            }

            ValidateTest(leg.Test, $"leg '{name}'", config, problems);
        }

        foreach (var (setName, legs) in config.LegSets)
        {
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

        foreach (var targetName in test.TestAgainstSshIfAvailable)
        {
            if (!config.Targets.TryGetValue(targetName, out var target))
            {
                problems.Add($"{owner} testAgainstSshIfAvailable names target '{targetName}', which is not declared");
            }
            else if (!string.Equals(target.Transport, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{owner} testAgainstSshIfAvailable names target '{targetName}', "
                    + "which does not use the ssh transport");
            }
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
        => config.Projects.Any(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase));

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
