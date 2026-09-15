using RepoHarness.Core.Projects;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// Builds the configuration <c>init</c> writes. The result is a working starting
/// point shaped to what the repository actually contains, not an empty skeleton.
/// </summary>
public static class DefaultConfigFactory
{
    /// <summary>Name the first detected project is declared under.</summary>
    private const string PrimaryProject = "main";

    /// <summary>
    /// Creates a starter configuration for the detected projects, with legs for the machine
    /// <c>init</c> runs on, named by its operating system and processor as measured.
    /// </summary>
    public static HarnessConfig Create(IReadOnlyList<DetectedProject> detected, string os, string processor)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentException.ThrowIfNullOrWhiteSpace(os);
        ArgumentException.ThrowIfNullOrWhiteSpace(processor);

        var config = new HarnessConfig
        {
            // The default project exists exactly when a project was detected to declare.
            Defaults = new HarnessDefaults { Project = detected.Count == 0 ? null : PrimaryProject },
            BuildConfigs =
            {
                ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug", DotnetConfiguration = "Debug", DartMode = "debug" },
                ["release"] = new BuildConfiguration { CmakeBuildType = "Release", DotnetConfiguration = "Release", DartMode = "release" },
            },
        };

        AddToolchains(config, detected);
        AddProjects(config, detected);
        AddLegs(config, os, processor);

        return config;
    }

    private static void AddToolchains(HarnessConfig config, IReadOnlyList<DetectedProject> detected)
    {
        // Only a native toolchain needs selecting. dotnet and dart resolve their own
        // compilers, so offering a toolchain axis for them would be noise.
        if (!detected.Any(project => project.Type == "cmake"))
        {
            return;
        }

        config.Toolchains["msvc"] = new ToolchainConfig
        {
            Platforms = ["windows"],
            Generator = "Ninja",
        };

        config.Toolchains["gcc"] = new ToolchainConfig
        {
            Platforms = ["linux", "windows"],
            Generator = "Ninja",
            Env = { ["CC"] = "gcc", ["CXX"] = "g++" },
        };

        config.Toolchains["clang"] = new ToolchainConfig
        {
            Platforms = ["all"],
            Generator = "Ninja",
            Env = { ["CC"] = "clang", ["CXX"] = "clang++" },
        };

        config.Sanitizers["asan"] = new VariantOverlay
        {
            Env =
            {
                ["CFLAGS"] = "-fsanitize=address,undefined",
                ["CXXFLAGS"] = "-fsanitize=address,undefined",
                ["LDFLAGS"] = "-fsanitize=address,undefined",
            },
        };
    }

    private static void AddProjects(HarnessConfig config, IReadOnlyList<DetectedProject> detected)
    {
        if (detected.Count == 0)
        {
            return;
        }

        var primary = detected[0];

        config.Projects.Add(new ProjectConfig
        {
            Name = PrimaryProject,
            Type = primary.Type,
            Path = primary.Path,
            DefaultToolchain = primary.Type == "cmake"
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["windows"] = "msvc",
                    ["linux"] = "gcc",
                    ["macos"] = "clang",
                }
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Test = CreateTestConfig(primary.Type),
        });
    }

    private static TestConfig CreateTestConfig(string projectType) => new()
    {
        Configs = ["debug"],
        All = projectType switch
        {
            "cmake" => new TestInvocation
            {
                Runner = "ctest",
                Args = ["--output-on-failure", "--no-tests=error"],
                FilterArg = "-R",

                // ctest reads this itself, so a -j someone adds to args still wins.
                CoresEnv = ["CTEST_PARALLEL_LEVEL"],
                SuccessPattern = "tests passed",
                CountPattern = @"tests failed out of (?<total>\d+)",
            },
            "dotnet" => new TestInvocation
            {
                Runner = "dotnet",
                Args = ["test"],
                FilterArg = "--filter",

                // The summary is matched as text, and dotnet otherwise translates it into the
                // machine's language: the same run would be witnessed on one host and not another.
                Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                },
                SuccessPattern = "Passed!",
                // Microsoft.Testing.Platform prints "total: 12"; VSTest pads "Total:     12".
                CountPattern = @"[Tt]otal:\s+(?<total>\d+)",
            },
            "dart" => new TestInvocation
            {
                Runner = "dart",
                Args = ["test"],
                FilterArg = "--name",
                CoresArgs = ["--concurrency={cores}"],
                SuccessPattern = "All tests passed!",
                CountPattern = @"\+(?<total>\d+)(?: ~\d+)?: All tests passed!",
            },
            _ => null,
        },
    };

    private static void AddLegs(HarnessConfig config, string os, string processor)
    {
        if (config.Projects.Count == 0)
        {
            return;
        }

        var names = new List<string>();

        foreach (var buildConfig in new[] { "debug", "release" })
        {
            // Named for what the leg needs rather than for the machine that wrote it, because the
            // file is shared: from another machine the same leg runs wherever such a host is found.
            var name = $"{os}-{processor}-{buildConfig}";

            config.Legs[name] = new LegConfig
            {
                Os = os,
                Processor = processor,
                Project = PrimaryProject,
                Config = buildConfig,
                Description = $"{buildConfig} build and test on {os} {processor}",
            };

            names.Add(name);
        }

        config.LegSets["gate"] = names;
    }
}
