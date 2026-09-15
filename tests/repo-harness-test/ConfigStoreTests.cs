using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void Load_Throws_WhenTheFileIsMissing()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ConfigException>(() => CreateStore().Load(temp.Combine("config.json")));

        Assert.Contains("init", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_Throws_WhenJsonIsMalformed()
    {
        var exception = LoadInvalid("{ this is not json");

        Assert.Contains("could not be read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingCommas()
    {
        var config = LoadValid("""
            {
              // config.json is hand edited, so this must parse.
              "version": 1,
              "defaults": { "buildCores": 8, },
            }
            """);

        Assert.Equal(1, config.Version);
        Assert.Equal(8, config.Defaults.BuildCores);
    }

    [Fact]
    public void Load_IsCaseInsensitiveOnPropertyNames()
    {
        var config = LoadValid("""{ "Version": 1, "Defaults": { "TestCores": 3 } }""");

        Assert.Equal(3, config.Defaults.TestCores);
    }

    [Fact]
    public void SaveThenLoad_PreservesEveryConfiguredSection()
    {
        using var temp = new TempDirectory();
        var store = CreateStore();
        var path = temp.Combine("config.json");

        var original = new HarnessConfig
        {
            Defaults = new HarnessDefaults { BuildCores = 12, TestCores = 4, MaxParallelLegs = 2, LegSet = "gate" },
            Toolchains = { ["clang"] = new ToolchainConfig { Env = { ["CXX"] = "clang++" } } },
            Sanitizers = { ["asan"] = new VariantOverlay { Env = { ["CFLAGS"] = "-fsanitize=address" } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Targets =
            {
                ["vps"] = new TargetConfig
                {
                    Transport = "ssh",
                    RepositoryPath = "/home/dev/repo",
                    AvailableOn = "HOST-A,HOST-B",
                    BuildCores = 32,
                    TestCores = 16,
                },
            },
            Legs =
            {
                ["vps-debug"] = new LegConfig { Target = "vps", Config = "debug", Toolchain = "clang", Sanitizer = "asan" },
            },
            LegSets = { ["gate"] = ["vps-debug"] },
            Exec = { ["fmt"] = new ExecConfig { Command = "clang-format", Args = ["-i"] } },
            Worktrees = new WorktreeSettings { MaxNameLength = 16, PathLimit = 1024 },
        };

        store.Save(path, original);
        var loaded = store.Load(path);

        Assert.Equal(12, loaded.Defaults.BuildCores);
        Assert.Equal(4, loaded.Defaults.TestCores);
        Assert.Equal(2, loaded.Defaults.MaxParallelLegs);
        Assert.Equal("gate", loaded.Defaults.LegSet);
        Assert.Equal("clang++", loaded.Toolchains["clang"].Env["CXX"]);
        Assert.Equal("-fsanitize=address", loaded.Sanitizers["asan"].Env["CFLAGS"]);
        Assert.Equal("Debug", loaded.BuildConfigs["debug"].CmakeBuildType);
        Assert.Equal("ssh", loaded.Targets["vps"].Transport);
        Assert.Equal("HOST-A,HOST-B", loaded.Targets["vps"].AvailableOn);
        Assert.Equal(32, loaded.Targets["vps"].BuildCores);
        Assert.Equal(16, loaded.Targets["vps"].TestCores);
        Assert.Equal("asan", loaded.Legs["vps-debug"].Sanitizer);
        Assert.Equal(["vps-debug"], loaded.LegSets["gate"]);
        Assert.Equal(["-i"], loaded.Exec["fmt"].Args);
        Assert.Equal(16, loaded.Worktrees.MaxNameLength);
        Assert.Equal(1024, loaded.Worktrees.PathLimit);
    }

    [Fact]
    public void Serialize_DoesNotEscapePlusSigns()
    {
        // The default HTML-safe encoder writes each plus sign as a + escape: valid
        // JSON, and unreadable to whoever maintains the file.
        var config = new HarnessConfig
        {
            Toolchains = { ["gcc"] = new ToolchainConfig { Env = { ["CXX"] = "g++" } } },
        };

        var json = CreateStore().Serialize(config);

        Assert.Contains("g++", json, StringComparison.Ordinal);
        Assert.DoesNotContain("u002B", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_OmitsEmptyCollections()
    {
        // A generated file full of "env": {} buries the settings that matter.
        var json = CreateStore().Serialize(new HarnessConfig());

        Assert.DoesNotContain("\"env\": {}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"projects\": []", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WritesLineFeeds_OnEveryPlatform()
    {
        // config.json is tracked; its bytes must not depend on which machine ran init.
        var json = CreateStore().Serialize(DefaultConfigFactory.Create([]));

        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedConfig_LooksUpKeysCaseInsensitively()
    {
        // Config keys are names a person typed. Looking up "Local-Debug" against a
        // config declaring "local-debug" must not report the leg as unknown, and the
        // in-memory config and the loaded one must not disagree about that.
        var config = LoadValid("""
            {
              "toolchains": { "msvc": { "generator": "Ninja" } },
              "buildConfigs": { "debug": { "cmakeBuildType": "Debug" } },
              "targets": { "root": { "transport": "local" } },
              "legs": { "local-debug": { "target": "root", "config": "debug" } },
              "legSets": { "gate": ["local-debug"] },
              "exec": { "fmt": { "command": "clang-format" } }
            }
            """);

        Assert.True(config.Toolchains.ContainsKey("MSVC"), "toolchains");
        Assert.True(config.BuildConfigs.ContainsKey("Debug"), "buildConfigs");
        Assert.True(config.Targets.ContainsKey("ROOT"), "targets");
        Assert.True(config.Legs.ContainsKey("Local-Debug"), "legs");
        Assert.True(config.LegSets.ContainsKey("Gate"), "legSets");
        Assert.True(config.Exec.ContainsKey("FMT"), "exec");
    }

    [Fact]
    public void LoadedConfig_ReplacesCollectionDefaults_RatherThanAppendingToThem()
    {
        // A toolchain declaring one platform must have exactly that platform. If
        // deserialization appended to the default instead of replacing it, a
        // Windows-only toolchain would silently become an everywhere toolchain.
        var config = LoadValid("""{ "toolchains": { "msvc": { "platforms": ["windows"] } } }""");

        Assert.Equal(["windows"], config.Toolchains["msvc"].Platforms);
    }

    [Fact]
    public void Load_RejectsAnUnknownKey_RatherThanSilentlyIgnoringIt()
    {
        // A misspelled key that is dropped reverts a setting to its default while the
        // file plainly appears to set it - the most frequent failure a config-driven
        // tool has, and the hardest to see.
        var exception = LoadInvalid("""{ "worktrees": { "pathBudgetReserv": 400 } }""");

        Assert.Contains("pathBudgetReserv", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAKeyDeclaredTwice_InDifferentCase()
    {
        // Looked up ignoring case, the two would be one entry, silently discarding one.
        var exception = LoadInvalid("""
            {
              "targets": { "root": { "transport": "local" } },
              "buildConfigs": { "debug": {} },
              "legs": {
                "local": { "target": "root", "config": "debug" },
                "LOCAL": { "target": "root", "config": "debug" }
              }
            }
            """);

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NamesAMissingRequiredSetting()
    {
        var exception = LoadInvalid("""{ "targets": { "vps": { "repositoryPath": "/srv/repo" } } }""");

        Assert.Contains("transport", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsNull_ForASettingThatCannotBeNull()
    {
        // Loaded, this would crash whatever first read the setting, far from the file.
        var exception = LoadInvalid("""{ "sync": { "neverTransfer": null } }""");

        Assert.Contains("neverTransfer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RejectsANullListItem_AndSaysWhichLine()
    {
        var exception = LoadInvalid("""
            {
              "legSets": {
                "gate": [null]
              }
            }
            """);

        Assert.Contains("cannot contain null", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsANullMapValue()
    {
        var exception = LoadInvalid("""{ "toolchains": { "msvc": null } }""");

        Assert.Contains("'msvc' is null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsANegativePathBudget_WhichWouldDisableTheGuard()
    {
        var exception = LoadInvalid("""{ "worktrees": { "pathBudgetReserve": -10000 } }""");

        Assert.Contains("disables the path budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAPathLimitBelowOne()
    {
        var exception = LoadInvalid("""{ "worktrees": { "pathLimit": 0 } }""");

        Assert.Contains("worktrees.pathLimit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsALegNamingSomethingUndeclared_ReportingEveryProblemAtOnce()
    {
        var exception = LoadInvalid("""
            {
              "targets": { "root": { "transport": "local" } },
              "buildConfigs": { "debug": { "cmakeBuildType": "Debug" } },
              "legs": { "bad": { "target": "typo", "config": "nope", "sanitizer": "tsan" } }
            }
            """);

        // Fixing one problem only to be shown the next is a poor way to correct a file.
        Assert.Contains("target 'typo'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("config 'nope'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sanitizer 'tsan'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsALegWorktreeThatIsNotAValidName()
    {
        var exception = LoadInvalid("""
            {
              "targets": { "root": { "transport": "local" } },
              "buildConfigs": { "debug": {} },
              "legs": { "in-worktree": { "target": "root", "config": "debug", "worktree": "Bad_Name" } }
            }
            """);

        Assert.Contains("leg 'in-worktree' worktree", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAnSshTargetWithNoRepositoryPath()
    {
        var exception = LoadInvalid("""{ "targets": { "vps": { "transport": "ssh" } } }""");

        Assert.Contains("repositoryPath", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAWslTargetWithNoDistro()
    {
        var exception = LoadInvalid("""{ "targets": { "wsl": { "transport": "wsl" } } }""");

        Assert.Contains("distro", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAnUnsupportedVersion()
    {
        var exception = LoadInvalid("""{ "version": 99 }""");

        Assert.Contains("version 99", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "defaults": { "buildCores": 0 } }""", "defaults.buildCores")]
    [InlineData("""{ "defaults": { "testCores": -1 } }""", "defaults.testCores")]
    [InlineData("""{ "defaults": { "maxParallelLegs": 0 } }""", "defaults.maxParallelLegs")]
    [InlineData("""{ "targets": { "vps": { "transport": "local", "buildCores": 0 } } }""", "target 'vps' buildCores")]
    [InlineData("""{ "targets": { "vps": { "transport": "local", "testCores": 0 } } }""", "target 'vps' testCores")]
    public void Load_RejectsACoreCountBelowOne(string json, string setting)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsATestSectionThatDeclaresNoInvocation()
    {
        var exception = LoadInvalid("""
            { "projects": [ { "name": "main", "type": "cmake", "test": { "configs": [] } } ] }
            """);

        Assert.Contains("declares no invocation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsTestingAgainstATargetThatDoesNotUseSsh()
    {
        var exception = LoadInvalid("""
            {
              "targets": { "root": { "transport": "local" } },
              "projects": [
                {
                  "name": "main",
                  "type": "cmake",
                  "test": { "all": { "runner": "ctest" }, "testAgainstSshIfAvailable": ["root"] }
                }
              ]
            }
            """);

        Assert.Contains("does not use the ssh transport", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsASuccessPatternThatIsNotARegularExpression()
    {
        // Found at load this is a clear message; found mid-run, a crash in an unrelated phase.
        var exception = LoadInvalid("""
            { "projects": [ { "name": "main", "type": "cmake", "test": { "all": { "runner": "ctest", "successPattern": "(" } } } ] }
            """);

        Assert.Contains("not a valid regular expression", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_AreUsable_WithoutAnyConfiguration()
    {
        var config = new HarnessConfig();

        Assert.Equal(6, HarnessDefaults.DefaultCores);
        Assert.Equal(HarnessDefaults.DefaultCores, config.Defaults.BuildCores);
        Assert.Equal(HarnessDefaults.DefaultCores, config.Defaults.TestCores);
        Assert.Null(config.Defaults.MaxParallelLegs);
        Assert.Equal(WorktreeSettings.DefaultMaxNameLength, config.Worktrees.MaxNameLength);
        Assert.True(config.Commit.SignOff);
        Assert.Empty(HarnessConfigValidator.Validate(config));
    }

    [Fact]
    public void TheSyncFloor_CannotBeRemovedByConfiguration()
    {
        // A floor held as a default value would be replaced by whatever list the file
        // declares, including an empty one, and sync would then carry .git and ssh secrets.
        var emptied = LoadValid("""{ "sync": { "neverTransfer": [] } }""");
        var replaced = LoadValid("""{ "sync": { "neverTransfer": ["out"] } }""");

        Assert.Equal(SyncConfig.NeverTransferFloor, emptied.Sync.EffectiveNeverTransfer);
        Assert.Equal([.. SyncConfig.NeverTransferFloor, "out"], replaced.Sync.EffectiveNeverTransfer);
        Assert.Contains(".git", SyncConfig.NeverTransferFloor);
        Assert.Contains(".harness-config", SyncConfig.NeverTransferFloor);
    }

    [Fact]
    public void SaveThenLoad_PreservesTheLegIntegritySettings()
    {
        using var temp = new TempDirectory();
        var store = CreateStore();
        var path = temp.Combine("config.json");

        var original = new HarnessConfig
        {
            Defaults = new HarnessDefaults
            {
                DurationWarningFactor = 2.5,
                ClockStepToleranceMilliseconds = 500,
                ProcessSampleSeconds = 2,
            },
            Contention = new ContentionConfig { BuildTools = ["ninja"], SharedResourceTools = ["compiler-daemon"] },
            Targets =
            {
                ["mac"] = new TargetConfig
                {
                    Transport = "ssh",
                    RepositoryPath = "/Users/dev/repo",
                    KeepAwake = ["caffeinate", "-dimsu", "-w", "{pid}"],
                    ConnectTimeoutSeconds = 10,
                    KeepAliveSeconds = 15,
                },
            },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "main",
                    Type = "cmake",
                    BuildOutputs = ["bin/tool"],
                    Test = new TestConfig
                    {
                        Inputs = ["config/**"],
                        All = new TestInvocation
                        {
                            Runner = "ctest",
                            CoresArgs = ["-j", "{cores}"],
                            CoresEnv = ["CTEST_PARALLEL_LEVEL"],
                            SuccessPattern = "tests passed",
                            CountPattern = @"out of (?<total>\d+)",
                        },
                    },
                },
            },
        };

        store.Save(path, original);
        var loaded = store.Load(path);

        Assert.Equal(2.5, loaded.Defaults.DurationWarningFactor);
        Assert.Equal(500, loaded.Defaults.ClockStepToleranceMilliseconds);
        Assert.Equal(2, loaded.Defaults.ProcessSampleSeconds);
        Assert.Equal(["ninja"], loaded.Contention.BuildTools);
        Assert.Equal(["compiler-daemon"], loaded.Contention.SharedResourceTools);
        Assert.Equal(["caffeinate", "-dimsu", "-w", "{pid}"], loaded.Targets["mac"].KeepAwake);
        Assert.Equal(10, loaded.Targets["mac"].ConnectTimeoutSeconds);
        Assert.Equal(15, loaded.Targets["mac"].KeepAliveSeconds);

        var project = Assert.Single(loaded.Projects);
        Assert.Equal(["bin/tool"], project.BuildOutputs);
        Assert.Equal(["config/**"], project.Test?.Inputs);
        Assert.Equal(["-j", "{cores}"], project.Test?.All?.CoresArgs);
        Assert.Equal(["CTEST_PARALLEL_LEVEL"], project.Test?.All?.CoresEnv);
        Assert.Equal(@"out of (?<total>\d+)", project.Test?.All?.CountPattern);
    }

    [Fact]
    public void LegIntegrityDefaults_ApplyWithoutConfiguration()
    {
        var config = new HarnessConfig();
        var target = new TargetConfig { Transport = "ssh" };

        Assert.Equal(3.0, config.Defaults.DurationWarningFactor);
        Assert.Equal(2000, config.Defaults.ClockStepToleranceMilliseconds);
        Assert.Equal(5, config.Defaults.ProcessSampleSeconds);
        Assert.Contains("ctest", config.Contention.BuildTools);
        Assert.Contains("ninja", config.Contention.BuildTools);
        Assert.Empty(config.Contention.SharedResourceTools);
        Assert.Equal(25, target.ConnectTimeoutSeconds);
        Assert.Equal(30, target.KeepAliveSeconds);
        Assert.Null(target.KeepAwake);
    }

    [Theory]
    [InlineData("""{ "defaults": { "durationWarningFactor": 0.5 } }""", "defaults.durationWarningFactor")]
    [InlineData("""{ "defaults": { "clockStepToleranceMilliseconds": -1 } }""", "defaults.clockStepToleranceMilliseconds")]
    [InlineData("""{ "defaults": { "processSampleSeconds": 0 } }""", "defaults.processSampleSeconds")]
    [InlineData("""{ "contention": { "buildTools": ["ninja", " "] } }""", "contention.buildTools contains a blank name")]
    [InlineData("""{ "targets": { "mac": { "transport": "local", "keepAwake": [] } } }""", "keepAwake has an empty command")]
    [InlineData("""{ "targets": { "vps": { "transport": "ssh", "repositoryPath": "/r", "connectTimeoutSeconds": 0 } } }""", "connectTimeoutSeconds")]
    [InlineData("""{ "targets": { "vps": { "transport": "ssh", "repositoryPath": "/r", "keepAliveSeconds": 0 } } }""", "keepAliveSeconds")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "buildOutputs": ["../other/bin"] } ] }""", "buildOutputs entry '../other/bin'")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "buildOutputs": ["/abs/bin"] } ] }""", "buildOutputs entry '/abs/bin'")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "buildOutputs": ["C:/bin"] } ] }""", "buildOutputs entry 'C:/bin'")]
    public void Load_RejectsALegIntegritySettingThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RequiresASuccessPattern_OnEveryPlatformATestRunsOn()
    {
        // A zero exit code alone has been measured, three separate times, to mean nothing ran.
        var exception = LoadInvalid("""
            { "projects": [ { "name": "main", "type": "cmake", "test": { "all": { "runner": "ctest" } } } ] }
            """);

        Assert.Contains("no successPattern for windows, linux, macos", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsPlatformSections_ThatSupplyWhatAllLeavesOut()
    {
        // Platform sections merge over 'all' field by field, so the requirement is checked on
        // the merged result rather than on each section alone.
        var config = LoadValid("""
            {
              "projects": [
                {
                  "name": "main",
                  "type": "cmake",
                  "test": {
                    "all": { "runner": "ctest" },
                    "windows": { "successPattern": "tests passed" },
                    "linux": { "successPattern": "tests passed" },
                    "macos": { "successPattern": "tests passed" }
                  }
                }
              ]
            }
            """);

        Assert.Equal("ctest", Assert.Single(config.Projects).Test?.All?.Runner);
    }

    [Fact]
    public void Load_RequiresARunner_ForAPlatformSectionThatStandsAlone()
    {
        var exception = LoadInvalid("""
            { "projects": [ { "name": "main", "type": "cmake", "test": { "linux": { "successPattern": "ok" } } } ] }
            """);

        Assert.Contains("names no runner for linux", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "runner": "ctest", "successPattern": "" }""", "successPattern is empty")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "countPattern": "out of (\\d+)" }""", "no named group 'total'")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "countPattern": "(" }""", "countPattern is not a valid regular expression")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "coresArgs": ["-j", "8"] }""", "never uses {cores}")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "coresEnv": [""] }""", "coresEnv contains a blank variable name")]
    public void Load_RejectsATestInvocationThatCannotWork(string invocation, string expected)
    {
        var exception = LoadInvalid(
            $$"""{ "projects": [ { "name": "main", "type": "cmake", "test": { "all": {{invocation}} } } ] }""");

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsATestInputOutsideTheTree()
    {
        var exception = LoadInvalid("""
            {
              "projects": [
                {
                  "name": "main",
                  "type": "cmake",
                  "test": { "inputs": ["../shared/**"], "all": { "runner": "ctest", "successPattern": "ok" } }
                }
              ]
            }
            """);

        Assert.Contains("test.inputs entry '../shared/**'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorSettings_DefaultToTheConventionalRegistries()
    {
        var anchors = new HarnessConfig().Anchors;

        Assert.Equal(".plans/_deferred-anchor-registry.md", anchors.PendingAnchorsPath);
        Assert.Equal(".plans/_deferred-anchor-registry-done.md", anchors.DoneAnchorsPath);
        Assert.Equal("D", anchors.IdPrefix);
        Assert.Equal(3, anchors.MinimumIdSegments);
    }

    [Fact]
    public void SeededConfiguration_NamesTheAnchorRegistries()
    {
        var json = CreateStore().Serialize(DefaultConfigFactory.Create([]));

        Assert.Contains("\"pendingAnchorsPath\": \".plans/_deferred-anchor-registry.md\"", json, StringComparison.Ordinal);
        Assert.Contains("\"doneAnchorsPath\": \".plans/_deferred-anchor-registry-done.md\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "../outside.md" } }""", "anchors.pendingAnchorsPath entry '../outside.md'")]
    [InlineData("""{ "anchors": { "doneAnchorsPath": "/abs/done.md" } }""", "anchors.doneAnchorsPath entry '/abs/done.md'")]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "notes/pending.txt" } }""", "must be a markdown (.md) file")]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "a.md", "doneAnchorsPath": "./A.md" } }""", "name the same file")]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "plans/work.md", "doneAnchorsPath": "plans/./work.md" } }""", "name the same file")]
    [InlineData("""{ "anchors": { "pendingAnchorsPath": "plans/work.md", "doneAnchorsPath": "plans//work.md" } }""", "name the same file")]
    [InlineData("""{ "anchors": { "idPrefix": "D-" } }""", "anchors.idPrefix")]
    [InlineData("""{ "anchors": { "minimumIdSegments": 0 } }""", "anchors.minimumIdSegments")]
    public void Load_RejectsAnchorSettingsThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    private static JsonConfigStore CreateStore() => new(new PhysicalFileSystem(FilePermissionsFactory.Create()));

    private static HarnessConfig LoadValid(string json)
    {
        using var temp = new TempDirectory();
        return CreateStore().Load(temp.WriteFile("config.json", json));
    }

    private static ConfigException LoadInvalid(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("config.json", json);

        return Assert.Throws<ConfigException>(() => CreateStore().Load(path));
    }
}
