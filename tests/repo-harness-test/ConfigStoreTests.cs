using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

public sealed class ConfigStoreTests
{
    /// <summary>An emulator declaration every leg test below can refer to.</summary>
    private const string QemuArm64 = """
        { "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "launcher": ["qemu-aarch64"], "witness": { "command": ["/opt/arm64/uname", "-m"], "pattern": "^aarch64$" } }
        """;

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
            Defaults = new HarnessDefaults { BuildCores = 12, TestCores = 4, MaxParallelLegs = 2 },
            Toolchains = { ["clang"] = new ToolchainConfig { Env = { ["CXX"] = "clang++" } } },
            Sanitizers = { ["asan"] = new VariantOverlay { Env = { ["CFLAGS"] = "-fsanitize=address" } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { BuildCores = 8 },
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "~/src/repo" } },
                Ssh = { ["vps"] = new SshHostConfig { RepositoryPath = "/home/dev/repo", BuildCores = 32, TestCores = 16 } },
            },
            SshItems = ["vps"],
            WslDistros = ["Ubuntu"],
            Emulators =
            {
                ["qemu-arm64"] = new EmulatorConfig
                {
                    HostOs = "linux",
                    HostProcessor = "x86_64",
                    Processor = "arm64",
                    Launcher = ["qemu-aarch64", "-L", "/usr/aarch64-linux-gnu"],
                    Requires = ["qemu-aarch64", "/usr/aarch64-linux-gnu"],
                    Witness = new EmulatorWitness { Command = ["/opt/arm64/uname", "-m"], Pattern = "^aarch64$" },
                },
            },
            Legs =
            {
                ["vps-debug"] = new LegConfig { Os = "linux", Processor = "x86_64", Ssh = "vps", Config = "debug", Toolchain = "clang", Sanitizer = "asan" },
                ["arm64-debug"] = new LegConfig { Os = "linux", Processor = "arm64", Emulator = "qemu-arm64", Wsl = "Ubuntu", Config = "debug" },
            },
            LegSets = { ["gate"] = ["vps-debug", "arm64-debug"] },
            Exec = { ["fmt"] = new ExecConfig { Command = "clang-format", Args = ["-i"] } },
            Worktrees = new WorktreeSettings { MaxNameLength = 16, PathLimit = 1024 },
        };

        store.Save(path, original);
        var loaded = store.Load(path);

        Assert.Equal(12, loaded.Defaults.BuildCores);
        Assert.Equal(4, loaded.Defaults.TestCores);
        Assert.Equal(2, loaded.Defaults.MaxParallelLegs);
        Assert.Equal("clang++", loaded.Toolchains["clang"].Env["CXX"]);
        Assert.Equal("-fsanitize=address", loaded.Sanitizers["asan"].Env["CFLAGS"]);
        Assert.Equal("Debug", loaded.BuildConfigs["debug"].CmakeBuildType);
        Assert.Equal(8, loaded.Hosts.Local.BuildCores);
        Assert.Equal("~/src/repo", loaded.Hosts.Wsl["Ubuntu"].RepositoryPath);
        Assert.Equal("/home/dev/repo", loaded.Hosts.Ssh["vps"].RepositoryPath);
        Assert.Equal(32, loaded.Hosts.Ssh["vps"].BuildCores);
        Assert.Equal(16, loaded.Hosts.Ssh["vps"].TestCores);
        Assert.Equal(["qemu-aarch64", "-L", "/usr/aarch64-linux-gnu"], loaded.Emulators["qemu-arm64"].Launcher);
        Assert.Equal(["qemu-aarch64", "/usr/aarch64-linux-gnu"], loaded.Emulators["qemu-arm64"].Requires);
        Assert.Equal(["test"], loaded.Emulators["qemu-arm64"].Phases);
        Assert.Equal("^aarch64$", loaded.Emulators["qemu-arm64"].Witness.Pattern);
        Assert.Equal("vps", loaded.Legs["vps-debug"].Ssh);
        Assert.Equal("asan", loaded.Legs["vps-debug"].Sanitizer);
        Assert.Equal("qemu-arm64", loaded.Legs["arm64-debug"].Emulator);
        Assert.Equal("Ubuntu", loaded.Legs["arm64-debug"].Wsl);
        Assert.Equal(["vps-debug", "arm64-debug"], loaded.LegSets["gate"]);
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
        var json = CreateStore().Serialize(DefaultConfigFactory.Create([], PlatformNames.Linux, PlatformNames.X64));

        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadedConfig_LooksUpKeysCaseInsensitively()
    {
        // Config keys are names a person typed. Looking up "Local-Debug" against a
        // config declaring "local-debug" must not report the leg as unknown, and the
        // in-memory config and the loaded one must not disagree about that.
        var config = LoadValid($$"""
            {
              "toolchains": { "msvc": { "generator": "Ninja", "env": { "CC": "cl" } } },
              "buildConfigs": { "debug": { "cmakeBuildType": "Debug" } },
              "sshItems": ["vps"], "wslDistros": ["Ubuntu"],
              "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "/home/dev/repo" } }, "ssh": { "vps": { "repositoryPath": "/srv/repo" } } },
              "emulators": { "qemu-arm64": {{QemuArm64}} },
              "legs": { "local-debug": { "os": "linux", "processor": "x86_64", "config": "debug" } },
              "legSets": { "gate": ["local-debug"] },
              "exec": { "fmt": { "command": "clang-format" } }
            }
            """);

        Assert.True(config.Toolchains.ContainsKey("MSVC"), "toolchains");
        Assert.True(config.BuildConfigs.ContainsKey("Debug"), "buildConfigs");
        Assert.True(config.Hosts.Wsl.ContainsKey("ubuntu"), "hosts.wsl");
        Assert.True(config.Hosts.Ssh.ContainsKey("VPS"), "hosts.ssh");
        Assert.True(config.Emulators.ContainsKey("QEMU-ARM64"), "emulators");
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
        var config = LoadValid("""{ "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" } } } }""");

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

    [Theory]
    [InlineData("""{ "targets": { "root": { "transport": "local" } } }""", "targets")]
    [InlineData("""{ "defaults": { "legSet": "gate" } }""", "legSet")]
    [InlineData("""{ "projects": [ { "name": "main", "type": "cmake", "test": { "all": { "runner": "ctest", "successPattern": "ok" }, "testAgainstSshIfAvailable": [] } } ] }""", "testAgainstSshIfAvailable")]
    public void Load_RejectsSettingsThatNoLongerExist(string json, string setting)
    {
        // Silently ignored, a file written for the old shape would lose its legs' hosts and its
        // default selection without a word.
        var exception = LoadInvalid(json);

        Assert.Contains(setting, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAKeyDeclaredTwice_InDifferentCase()
    {
        // Looked up ignoring case, the two would be one entry, silently discarding one.
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "legs": {
                "local": { "os": "linux", "processor": "x86_64", "config": "debug" },
                "LOCAL": { "os": "linux", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NamesAMissingRequiredSetting()
    {
        var exception = LoadInvalid("""{ "hosts": { "ssh": { "vps": { "buildCores": 2 } } } }""");

        Assert.Contains("repositoryPath", exception.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// How check-ci-legs reads a workflow is held when the file is read: a pattern that does not compile, or
    /// lacks the group it is read by, a step named by nothing, and a workflow pattern naming no leg, which
    /// would find the same budget for every one.
    /// </summary>
    [Theory]
    [InlineData("""{ "ci": { "legJobPattern": "unit (" } }""", "ci.legJobPattern is not a valid regular expression")]
    [InlineData("""{ "ci": { "legJobPattern": "^unit \\((?<name>[^)]+)\\)" } }""", "ci.legJobPattern has no named group 'leg' to capture the leg's name")]
    [InlineData("""{ "ci": { "buildStep": " " } }""", "ci.buildStep is given empty, or as spaces alone")]
    [InlineData("""{ "ci": { "testStep": "" } }""", "ci.testStep is given empty, or as spaces alone")]
    [InlineData("""{ "ci": { "workflowBudgetPattern": "minutes: (?<budget>[0-9]+)" } }""", "ci.workflowBudgetPattern has no {leg}")]
    [InlineData("""{ "ci": { "workflowBudgetPattern": "{leg}: (?<minutes>[0-9]+)" } }""", "ci.workflowBudgetPattern has no named group 'budget'")]
    [InlineData("""{ "ci": { "workflowBudgetPattern": "{leg}: (?<budget>[0-9]+" } }""", "ci.workflowBudgetPattern is not a valid regular expression")]
    [InlineData("""{ "ci": { "legBudgetMinutes": -1 } }""", "ci.legBudgetMinutes cannot be negative, found -1")]
    public void Load_RejectsCiConventionsThatCannotBeRead(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsCiConventions_ThatCanBeRead()
    {
        var config = LoadValid("""{ "ci": { "legJobPattern": "^unit \\((?<leg>[^,)]+)", "buildStep": "Build", "testStep": "Test", "workflowBudgetPattern": "leg: {leg}, minutes: (?<budget>[0-9]+)" } }""");

        Assert.Equal("^unit \\((?<leg>[^,)]+)", config.Ci.LegJobPattern);
        Assert.Equal("leg: {leg}, minutes: (?<budget>[0-9]+)", config.Ci.WorkflowBudgetPattern);
    }

    /// <summary>A host's wake window is some seconds or none: a negative one is no window.</summary>
    [Fact]
    public void Load_RejectsANegativeWakeWindow()
    {
        var exception = LoadInvalid(
            "{ \"sshItems\": [\"mac\"], \"hosts\": { \"ssh\": { \"mac\": { \"repositoryPath\": \"/Users/me/repo\", \"wakeWaitSeconds\": -1 } } } }");

        Assert.Contains("hosts.ssh 'mac' wakeWaitSeconds cannot be negative, found -1", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A leg's buildSpaceGiB is a positive number of GiB: none, or less, is no need anybody measured.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    public void Load_RejectsABuildSpaceThatIsNoRoom(string room)
    {
        var exception = LoadInvalid(
            "{ \"buildConfigs\": { \"debug\": {} }, \"legs\": { \"native\": { \"os\": \"linux\", \"processor\": \"x86_64\", \"config\": \"debug\", \"buildSpaceGiB\": "
            + room + " } } }");

        Assert.Contains($"leg 'native' buildSpaceGiB must be a positive number of GiB, found {room}", exception.Message, StringComparison.Ordinal);
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
              "buildConfigs": { "debug": { "cmakeBuildType": "Debug" } },
              "legs": { "bad": { "os": "linux", "processor": "x86_64", "ssh": "typo", "config": "nope", "sanitizer": "tsan" } }
            }
            """);

        // Fixing one problem only to be shown the next is a poor way to correct a file.
        Assert.Contains("ssh host 'typo'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("config 'nope'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("sanitizer 'tsan'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsALegWorktreeThatIsNotAValidName()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "legs": { "in-worktree": { "os": "linux", "processor": "x86_64", "config": "debug", "worktree": "Bad_Name" } }
            }
            """);

        Assert.Contains("leg 'in-worktree' worktree", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "os": "linx", "processor": "x86_64", "config": "debug" }""", "leg 'x' os is 'linx'")]
    [InlineData("""{ "os": "linux", "processor": "amd64", "config": "debug" }""", "leg 'x' processor is 'amd64'")]
    [InlineData("""{ "os": "linux", "processor": "x86_64", "config": "debug", "wsl": "Ubuntu", "ssh": "vps" }""", "names both a wsl and an ssh host")]
    [InlineData("""{ "os": "windows", "processor": "x86_64", "config": "debug", "wsl": "Ubuntu" }""", "names wsl host 'Ubuntu', which runs linux")]
    [InlineData("""{ "os": "linux", "processor": "x86_64", "config": "debug", "wsl": "Debian" }""", "wsl host 'Debian', which is not declared under hosts.wsl")]
    [InlineData("""{ "os": "linux", "processor": "arm64", "config": "debug", "emulator": "missing" }""", "emulator 'missing', which is not declared")]
    [InlineData("""{ "os": "linux", "processor": "riscv64", "config": "debug", "emulator": "qemu-arm64" }""", "but emulator 'qemu-arm64' runs arm64 programs")]
    [InlineData("""{ "os": "macos", "processor": "arm64", "config": "debug", "emulator": "qemu-arm64" }""", "but emulator 'qemu-arm64' runs on linux hosts")]
    public void Load_RejectsALegThatCanNeverBePlaced(string leg, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "buildConfigs": { "debug": {} },
              "sshItems": ["vps"], "wslDistros": ["Ubuntu"],
              "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "/home/dev/repo" } }, "ssh": { "vps": { "repositoryPath": "/srv/repo" } } },
              "emulators": { "qemu-arm64": {{QemuArm64}} },
              "legs": { "x": {{leg}} }
            }
            """);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsALegThatNamesNoHost_BecauseWhereItRunsIsMeasured()
    {
        var config = LoadValid($$"""
            {
              "buildConfigs": { "debug": {} },
              "emulators": { "qemu-arm64": {{QemuArm64}} },
              "legs": { "arm64": { "os": "linux", "processor": "arm64", "emulator": "qemu-arm64", "config": "debug" } }
            }
            """);

        var leg = config.Legs["arm64"];
        Assert.Null(leg.Wsl);
        Assert.Null(leg.Ssh);
    }

    [Fact]
    public void Load_RejectsALegSetNamedLikeALeg()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "legs": { "gate": { "os": "linux", "processor": "x86_64", "config": "debug" } },
              "legSets": { "gate": ["gate"] }
            }
            """);

        Assert.Contains("legSet 'gate' has the same name as a leg", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "legs": { "a,b": { "os": "linux", "processor": "x86_64", "config": "debug" } } }""", "leg 'a,b' cannot be selected with --legs")]
    [InlineData("""{ "legs": { "a b": { "os": "linux", "processor": "x86_64", "config": "debug" } } }""", "leg 'a b' cannot be selected with --legs")]
    [InlineData("""{ "legs": { "gate": { "os": "linux", "processor": "x86_64", "config": "debug" } }, "legSets": { "x,y": ["gate"] } }""", "legSet 'x,y' cannot be selected with --legs")]
    [InlineData("""{ "legs": { "gate": { "os": "linux", "processor": "x86_64", "config": "debug" } }, "legSets": { "set": ["missing"] } }""", "legSet 'set' names leg 'missing', which is not declared")]
    public void Load_RejectsALegOrLegSet_ThatLegsCouldNeverSelect(string json, string expected)
    {
        // --legs splits its value at commas and the command line at spaces, so such a name is unreachable.
        var exception = LoadInvalid(json.Replace("{ \"legs\"", "{ \"buildConfigs\": { \"debug\": {} }, \"legs\"", StringComparison.Ordinal));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "repo" } } } }""", "repositoryPath 'repo' must be absolute")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "~" } } } }""", "is a home or root directory")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "~/" } } } }""", "is a home or root directory")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/" } } } }""", "is a home or root directory")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "C:\\" } } } }""", "is a home or root directory")]
    [InlineData("""{ "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "C:\\src\\repo" } } } }""", "start with ~/ for the home directory inside the distribution")]
    [InlineData("""{ "hosts": { "ssh": { "-oProxyCommand=x": { "repositoryPath": "/r" } } } }""", "is not a usable name")]
    [InlineData("""{ "hosts": { "wsl": { "my distro": { "repositoryPath": "/r" } } } }""", "is not a usable name")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "~/src/.." } } } }""", "has a '.' or '..' segment")]
    [InlineData("""{ "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "/home/dev/./repo" } } } }""", "has a '.' or '..' segment")]
    public void Load_RejectsAHostThatCannotBeUsed(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/home/dev/repo")]
    [InlineData("~/src/repo")]
    [InlineData("C:\\\\src\\\\repo")]
    [InlineData("D:/src/repo")]
    public void Load_AcceptsAnSshRepositoryPath_ThatNamesItsDirectory(string path)
    {
        var config = LoadValid($$"""{ "sshItems": ["vps"], "hosts": { "ssh": { "vps": { "repositoryPath": "{{path}}" } } } }""");

        Assert.True(config.Hosts.Ssh.ContainsKey("vps"));
    }

    [Theory]
    [InlineData("""{ "hostOs": "beos", "hostProcessor": "x86_64", "processor": "arm64", "witness": { "command": ["w"], "pattern": "x" } }""", "emulator 'e' hostOs is 'beos'")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "x86_64", "witness": { "command": ["w"], "pattern": "x" } }""", "which needs no emulator")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "launcher": [], "witness": { "command": ["w"], "pattern": "x" } }""", "launcher is empty")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "phases": ["deploy"], "witness": { "command": ["w"], "pattern": "x" } }""", "phases names 'deploy'")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "phases": [], "witness": { "command": ["w"], "pattern": "x" } }""", "phases is empty")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "witness": { "command": [], "pattern": "x" } }""", "witness has an empty command")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "witness": { "command": ["w"], "pattern": "(" } }""", "witness.pattern is not a valid regular expression")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "tool": "qemu", "witness": { "command": ["w"], "pattern": "x" } }""", "names tool 'qemu', which is not declared")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "requires": [" "], "witness": { "command": ["w"], "pattern": "x" } }""", "requires contains a blank entry")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "launcher": ["./qemu-aarch64"], "witness": { "command": ["w"], "pattern": "x" } }""", "launcher './qemu-aarch64' must be a program name, looked up on the PATH, or an absolute path")]
    [InlineData("""{ "hostOs": "windows", "hostProcessor": "arm64", "processor": "x86_64", "launcher": ["tools\\prism.exe"], "witness": { "command": ["w"], "pattern": "x" } }""", "launcher 'tools\\prism.exe' must be a program name")]
    [InlineData("""{ "hostOs": "windows", "hostProcessor": "arm64", "processor": "x86_64", "launcher": ["C:prism.exe"], "witness": { "command": ["w"], "pattern": "x" } }""", "launcher 'C:prism.exe' must be a program name")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "witness": { "command": ["bin/uname"], "pattern": "x" } }""", "witness 'bin/uname' must be a program name")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "requires": ["~/sysroot"], "witness": { "command": ["w"], "pattern": "x" } }""", "requires '~/sysroot' must be a program name")]
    [InlineData("""{ "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64", "launcher": ["qemu-aarch64"], "witness": { "command": ["uname", "-m"], "pattern": "x" } }""", "witness 'uname' must be an absolute path: behind a launcher it is found by the launcher")]
    public void Load_RejectsAnEmulatorThatCannotWork(string emulator, string expected)
    {
        var exception = LoadInvalid($$"""{ "emulators": { "e": {{emulator}} } }""");

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qemu-aarch64")]
    [InlineData("/usr/bin/qemu-aarch64")]
    [InlineData("C:/Tools/qemu-aarch64.exe")]
    [InlineData("C:\\\\Tools\\\\qemu-aarch64.exe")]
    public void Load_AcceptsAnEmulatorProgram_NamedOrGivenByAnAbsolutePath(string program)
    {
        var config = LoadValid($$"""
            { "emulators": { "e": {
                "hostOs": "linux", "hostProcessor": "x86_64", "processor": "arm64",
                "launcher": ["{{program}}"], "requires": ["{{program}}"],
                "witness": { "command": ["/opt/arm64/uname"], "pattern": "x" } } } }
            """);

        Assert.True(config.Emulators.ContainsKey("e"));
    }

    [Fact]
    public void Load_AcceptsAWitnessNamedWithoutAPath_WhenNoLauncherStandsBeforeIt()
    {
        // With no launcher the witness is started like any program, so it is looked up on the PATH.
        var config = LoadValid("""
            { "emulators": { "prism": {
                "hostOs": "windows", "hostProcessor": "arm64", "processor": "x86_64",
                "witness": { "command": ["x64-witness"], "pattern": "x" } } } }
            """);

        Assert.Null(config.Emulators["prism"].Launcher);
    }

    [Theory]
    [InlineData("""{ "commit": { "template": "{area}: {summary}", "variables": { "area": {} } } }""", "commit.template uses {summary}, which is not declared")]
    [InlineData("""{ "commit": { "template": "{area}: fix", "variables": { "area": {}, "scope": {} } } }""", "commit.variables 'scope' is never used")]
    [InlineData("""{ "commit": { "variables": { "area": {} } } }""", "there is no commit.template to use them")]
    [InlineData("""{ "commit": { "template": "{area}", "variables": { "area": { "required": true, "default": "core" } } } }""", "is required and has a default")]
    [InlineData("""{ "commit": { "template": "{1area}", "variables": { "1area": {} } } }""", "commit.variables '1area' must be letters")]
    [InlineData("""{ "commit": { "template": " " } }""", "commit.template is blank")]
    public void Load_RejectsACommitPolicyThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsACommitTemplate_WhoseEveryPlaceholderIsDeclared()
    {
        var config = LoadValid("""
            { "commit": { "template": "{area}: {summary}", "variables": { "area": { "required": true }, "summary": { "default": "update" } } } }
            """);

        Assert.Equal(2, config.Commit.Variables.Count);
    }

    [Theory]
    [InlineData("""{ "sync": { "exclude": ["../outside"] } }""", "sync.exclude entry '../outside'")]
    [InlineData("""{ "sync": { "neverTransfer": ["/etc"] } }""", "sync.neverTransfer entry '/etc'")]
    [InlineData("""{ "sync": { "exclude": [""] } }""", "sync.exclude entry ''")]
    public void Load_RejectsASyncPathOutsideTheTree(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worktrees root or a sync path spelled with a '.' segment or a doubled separator inside it is
    /// compared as written - by sync's lists, and by init's ignore rule for the root - and so would
    /// withhold and ignore nothing it names: refused, with the one spelling to write.
    /// </summary>
    [Theory]
    [InlineData("""{ "worktrees": { "root": ".harness-config/./worktrees" } }""", "worktrees.root names '.harness-config/./worktrees'", ".harness-config/worktrees")]
    [InlineData("""{ "sync": { "neverTransfer": ["build//x"] } }""", "sync.neverTransfer names 'build//x'", "build/x")]
    [InlineData("""{ "sync": { "exclude": ["docs/./old"] } }""", "sync.exclude names 'docs/./old'", "docs/old")]
    public void Load_RejectsAPathSpelledTwoWays_NamingTheOneSpelling(string json, string named, string spelling)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(named, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"write '{spelling}'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worktrees root that is the tree itself is refused as that, and only as that: it is no path
    /// spelled two ways, and a line saying to write '' would send the reader nowhere.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void Load_RejectsAWorktreesRootThatIsTheTreeItself_AsThatAlone(string root)
    {
        var exception = LoadInvalid($$"""{ "worktrees": { "root": "{{root}}" } }""");

        Assert.Contains("worktrees.root cannot be the repository root itself", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("doubled separator", exception.Message, StringComparison.Ordinal);
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
    [InlineData("""{ "hosts": { "local": { "buildCores": 0 } } }""", "hosts.local buildCores")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/r", "testCores": 0 } } } }""", "hosts.ssh 'vps' testCores")]
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

        // The harness's own state is withheld by code rather than by that list, so emptying the list
        // reaches it no more than it reaches .git: a host's credentials are neither sent nor deleted.
        var exclusions = new RepoHarness.Core.Sync.SyncExclusions(emptied.Sync, emptied.Worktrees.Root);

        foreach (var local in new[] { ".git/config", ".harness-config/sshItems/vps/.env", ".harness-config/runner/.secrets/ci.env" })
        {
            Assert.True(exclusions.IsWithheldFromTransfer(local), $"'{local}' should never be sent");
            Assert.True(exclusions.IsProtectedFromDeletion(local), $"'{local}' should never be deleted");
        }
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
            SshItems = ["mac"],
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["mac"] = new SshHostConfig
                    {
                        RepositoryPath = "/Users/dev/repo",
                        KeepAwake = ["caffeinate", "-dimsu", "-w", "{pid}"],
                        ConnectTimeoutSeconds = 10,
                        KeepAliveSeconds = 15,
                    },
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
        Assert.Equal(["caffeinate", "-dimsu", "-w", "{pid}"], loaded.Hosts.Ssh["mac"].KeepAwake);
        Assert.Equal(10, loaded.Hosts.Ssh["mac"].ConnectTimeoutSeconds);
        Assert.Equal(15, loaded.Hosts.Ssh["mac"].KeepAliveSeconds);

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
        var host = new SshHostConfig { RepositoryPath = "/srv/repo" };

        Assert.Equal(3.0, config.Defaults.DurationWarningFactor);
        Assert.Equal(2000, config.Defaults.ClockStepToleranceMilliseconds);
        Assert.Equal(5, config.Defaults.ProcessSampleSeconds);
        Assert.Contains("ctest", config.Contention.BuildTools);
        Assert.Contains("ninja", config.Contention.BuildTools);
        Assert.Empty(config.Contention.SharedResourceTools);
        Assert.Equal(25, host.ConnectTimeoutSeconds);
        Assert.Equal(30, host.KeepAliveSeconds);
        Assert.Null(host.KeepAwake);
    }

    [Theory]
    [InlineData("""{ "defaults": { "durationWarningFactor": 0.5 } }""", "defaults.durationWarningFactor")]
    [InlineData("""{ "defaults": { "clockStepToleranceMilliseconds": -1 } }""", "defaults.clockStepToleranceMilliseconds")]
    [InlineData("""{ "defaults": { "processSampleSeconds": 0 } }""", "defaults.processSampleSeconds")]
    [InlineData("""{ "contention": { "buildTools": ["ninja", " "] } }""", "contention.buildTools contains a blank name")]
    [InlineData("""{ "hosts": { "local": { "keepAwake": [] } } }""", "keepAwake has an empty command")]
    [InlineData("""{ "hosts": { "local": { "keepAwake": ["caffeinate", "-w", "{leg}"] } } }""", "keepAwake names '{leg}', which nothing fills in: it can hold only {pid}")]
    [InlineData("""{ "hosts": { "local": { "keepAwake": ["./awake.sh"] } } }""", "keepAwake './awake.sh' must be a program name")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/r", "connectTimeoutSeconds": 0 } } } }""", "connectTimeoutSeconds")]
    [InlineData("""{ "hosts": { "ssh": { "vps": { "repositoryPath": "/r", "keepAliveSeconds": 0 } } } }""", "keepAliveSeconds")]
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
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "testSet": " " }""", "testSet is blank; leave it out for the project's shared test set")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state", " "] }""", "remoteExcludes cannot reach the runner on windows, linux, macos: An exclusion was given empty, or as spaces alone")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "remoteExcludes": ["git-state"] }""", "remoteExcludes cannot reach the runner on windows, linux, macos: An exclusion was given, but the test settings declare no excludeArg")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "excludeArg": "-LE", "remoteExcludes": ["git-state"] }""", "with no excludeJoin it would take them apart, never leaving out what each names; declare \"excludeJoin\": \"|\"")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["--rerun-failed"], "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state"] }""", "run ctest with --rerun-failed")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["-U", "ON"], "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state"] }""", "run ctest with --union")]
    public void Load_RejectsATestInvocationThatCannotWork(string invocation, string expected)
    {
        var exception = LoadInvalid(
            $$"""{ "projects": [ { "name": "main", "type": "cmake", "test": { "all": {{invocation}} } } ] }""");

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// remoteExcludes that can reach the runner as declared load: joined for ctest, by -E beside a union in
    /// the args, and beside a test preset, whose files are read as the leg starts, not here.
    /// </summary>
    [Theory]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state", "gpu"] }""")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["-U", "ON"], "excludeArg": "-E", "excludeJoin": "|", "remoteExcludes": ["git_state"] }""")]
    [InlineData("""{ "runner": "ctest", "successPattern": "ok", "args": ["--preset", "ci"], "excludeArg": "-LE", "excludeJoin": "|", "remoteExcludes": ["git-state"] }""")]
    [InlineData("""{ "runner": "dart", "successPattern": "ok", "excludeArg": "--exclude-tags", "remoteExcludes": ["git-state", "gpu"] }""")]
    public void Load_AcceptsRemoteExcludes_ThatCanReachTheRunnerAsDeclared(string invocation)
    {
        var config = LoadValid(
            $$"""{ "projects": [ { "name": "main", "type": "cmake", "test": { "all": {{invocation}} } } ] }""");

        Assert.NotEmpty(Assert.Single(config.Projects).Test!.All!.RemoteExcludes!);
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
        var json = CreateStore().Serialize(DefaultConfigFactory.Create([], PlatformNames.Linux, PlatformNames.X64));

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

    /// <summary>
    /// Every way a runner's action can be spelled wrong, refused when the file is read rather than
    /// when a runner is finally invoked. The rule needs no file system, so it belongs here: a
    /// configuration <c>legs</c> calls valid is one <c>run</c> can act on, and the promise that
    /// every problem is listed at once covers this one too.
    /// </summary>
    [Theory]
    [InlineData("corpus.yml", "corpus/corpus.yml")]
    [InlineData("corpus.yaml", "corpus/corpus.yml")]
    [InlineData("../outside.yml", "without '.' or '..'")]
    [InlineData("a/../../outside.yml", "without '.' or '..'")]
    [InlineData("./corpus/corpus.yml", "without '.' or '..'")]
    [InlineData("/etc/passwd.yml", "absolute path")]
    [InlineData("C:/windows/evil.yml", "absolute path")]
    [InlineData("corpus/nested/corpus.yml", "carries its directory's name")]
    [InlineData("corpus/steps.yml", "carries its directory's name")]
    [InlineData("corpus/corpus.txt", "does not end in")]
    public void Load_RejectsARunnerActionThatIsNotOneDirectoryPerAction(string action, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "predefinedRunners": { "corpus": { "action": {{System.Text.Json.JsonSerializer.Serialize(action)}} } }
            }
            """);

        Assert.Contains("predefined runner 'corpus' action", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a runner's own steps cannot say, refused when the file is read: steps on a runner of phases,
    /// which has no step to name; none at all, which would run nothing; and a step named blank or twice.
    /// Which steps its action declares is asked when that file is read.
    /// </summary>
    [Theory]
    [InlineData("""{ "phases": [ { "name": "go", "command": ["dotnet", "--info"] } ], "steps": ["go"] }""", "names steps, which only an action file has")]
    [InlineData("""{ "action": "corpus/corpus.yml", "steps": [] }""", "names no step under steps, so it would run nothing")]
    [InlineData("""{ "action": "corpus/corpus.yml", "steps": [" "] }""", "names a blank step under steps")]
    [InlineData("""{ "action": "corpus/corpus.yml", "steps": ["bench", "bench"] }""", "names step 'bench' more than once under steps")]
    public void Load_RejectsARunnersStepsThatCannotBeRun(string runner, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "predefinedRunners": { "corpus": {{runner}} }
            }
            """);

        Assert.Contains($"predefined runner 'corpus' {expected}", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A runner naming steps of its action loads, and keeps them in the order written.</summary>
    [Fact]
    public void Load_ReadsARunnersOwnSteps()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "config.json");
        File.WriteAllText(path, """
            {
              "predefinedRunners": { "bench": { "action": "corpus/corpus.yml", "steps": ["bench", "profile"] } }
            }
            """);

        var config = new JsonConfigStore(new PhysicalFileSystem(FilePermissionsFactory.Create())).Load(path);

        Assert.Equal(["bench", "profile"], config.PredefinedRunners["bench"].Steps);
    }

    /// <summary>
    /// Directories above an action's own group actions and are the author's to arrange. A corpus
    /// large enough to be a harness of its own is unreadable as a flat pile of names carrying their
    /// grouping as a prefix, and what identifies an action is unchanged: the file carries the name
    /// of the directory that directly contains it.
    /// </summary>
    [Theory]
    [InlineData("corpus/corpus.yml")]
    [InlineData("corpus/corpus.yaml")]
    [InlineData("real-examples/sqlite/sqlite.yml")]
    [InlineData("real-examples/c/probe-nest/probe-nest.yml")]
    public void Load_AcceptsARunnerActionInItsOwnDirectory(string action)
    {
        var config = LoadValid($$"""
            {
              "predefinedRunners": { "corpus": { "action": {{System.Text.Json.JsonSerializer.Serialize(action)}} } }
            }
            """);

        Assert.Equal(action, config.PredefinedRunners["corpus"].Action);
    }

    /// <summary>
    /// A ceiling below the per-machine cap makes the per-machine number a claim nothing can honour,
    /// so the file would say one thing and the run show another.
    /// </summary>
    [Fact]
    public void Load_RejectsAFleetCeilingBelowThePerMachineCap()
    {
        var exception = LoadInvalid("""
            { "defaults": { "maxParallelLegs": 4, "maxParallelLegsTotal": 2 } }
            """);

        Assert.Contains("maxParallelLegsTotal", exception.Message, StringComparison.Ordinal);
        Assert.Contains("maxParallelLegs", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "defaults": { "maxParallelLegsTotal": 0 } }""", "defaults.maxParallelLegsTotal")]
    [InlineData("""{ "testTimingRegex": ["(unclosed"] }""", "testTimingRegex[0]")]
    public void Load_RejectsParallelismAndTimingSettingsThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsAFleetCeilingAtOrAboveThePerMachineCap()
    {
        var config = LoadValid("""
            { "defaults": { "maxParallelLegs": 2, "maxParallelLegsTotal": 6 } }
            """);

        Assert.Equal(2, config.Defaults.MaxParallelLegs);
        Assert.Equal(6, config.Defaults.MaxParallelLegsTotal);
    }

    /// <summary>
    /// A keyed entry that covers no platform some leg builds on would leave that leg with one
    /// fewer witness than the file appears to give it, which is the failure buildOutputs exists to
    /// prevent. Refused when the file is read, naming the leg: every declared leg and its operating
    /// system are known then.
    /// </summary>
    [Fact]
    public void Load_RejectsABuildOutputThatNoLegsPlatformIsCoveredBy()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "projects": [
                { "name": "app", "type": "cmake", "buildOutputs": [ { "windows": "bin/app.exe" } ] }
              ],
              "legs": {
                "win": { "os": "windows", "processor": "x86_64", "config": "debug" },
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        Assert.Contains("names no path for 'linux'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("leg 'nix'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsAKeyedBuildOutputThatCoversEveryLeg()
    {
        var config = LoadValid("""
            {
              "buildConfigs": { "debug": {} },
              "projects": [
                { "name": "app", "type": "cmake",
                  "buildOutputs": [ "compile_commands.json", { "windows": "bin/app.exe", "all": "bin/app" } ] }
              ],
              "legs": {
                "win": { "os": "windows", "processor": "x86_64", "config": "debug" },
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        var outputs = Assert.Single(config.Projects).BuildOutputs;

        Assert.Equal(2, outputs.Count);
        Assert.Equal("compile_commands.json", outputs[0].For("linux"));
        Assert.Equal("bin/app.exe", outputs[1].For("windows"));
        Assert.Equal("bin/app", outputs[1].For("linux"));
    }

    [Theory]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "freebsd": "bin/a" } ] } ] }""", "expected one of")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "all": "/etc/passwd" } ] } ] }""", "relative path")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "all": "../escape" } ] } ] }""", "relative path")]
    public void Load_RejectsABuildOutputThatCannotWork(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare string is what every configuration written before platform keying used, and it still
    /// means the same thing, written back the same way.
    /// </summary>
    [Fact]
    public void ABuildOutput_WrittenAsAString_RoundTripsAsAString()
    {
        using var temp = new TempDirectory();
        var store = CreateStore();
        var path = temp.Combine("config.json");

        store.Save(path, new HarnessConfig
        {
            Projects = { new ProjectConfig { Name = "a", Type = "cmake", BuildOutputs = ["bin/a"] } },
        });

        Assert.Contains("\"bin/a\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal([(BuildOutput)"bin/a"], Assert.Single(store.Load(path).Projects).BuildOutputs);
    }

    /// <summary>
    /// A tool needed only on one platform stops being reported missing on the others. Without this
    /// a repository declaring both MSVC and a POSIX compiler could never have every leg provisioned.
    /// </summary>
    [Fact]
    public void ATool_MayNameThePlatformsItIsNeededOn()
    {
        var config = LoadValid("""
            {
              "tools": [
                { "name": "cl", "platforms": ["windows"] },
                { "name": "cmake" }
              ]
            }
            """);

        Assert.Equal(["windows"], config.Tools[0].Platforms);
        Assert.Empty(config.Tools[1].Platforms);
    }

    [Fact]
    public void Load_RejectsAToolPlatformThatIsNotOne()
    {
        var exception = LoadInvalid("""{ "tools": [ { "name": "cl", "platforms": ["windoze"] } ] }""");

        Assert.Contains("windoze", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A toolchain's platforms used to be validated for spelling and then read by nothing, so a leg
    /// naming a compiler that does not exist on its own operating system was attempted anyway and
    /// failed much later as a missing program.
    /// </summary>
    [Fact]
    public void Load_RejectsALegNamingAToolchainThatDoesNotExistOnItsPlatform()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" } } },
              "legs": {
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "msvc" }
              }
            }
            """);

        Assert.Contains("leg 'nix'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist on 'linux'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "platforms": ["linux"], "env": { "CC": "gcc" } }""")]
    [InlineData("""{ "platforms": ["all"], "env": { "CC": "gcc" } }""")]
    [InlineData("""{ "env": { "CC": "gcc" } }""")]
    public void Load_AcceptsALegWhoseToolchainExistsOnItsPlatform(string toolchain)
    {
        var config = LoadValid($$"""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": {{toolchain}} },
              "legs": {
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "gcc" }
              }
            }
            """);

        Assert.Equal("gcc", config.Legs["nix"].Toolchain);
    }

    /// <summary>
    /// The same contradiction reached through a project's default rather than a leg's own name.
    /// A key naming one platform says which platform it is for; <c>all</c> does not, so it is
    /// checked against the operating systems the legs building that project declare.
    /// </summary>
    [Theory]
    [InlineData("windows", "defaultToolchain['windows']")]
    [InlineData("all", "defaultToolchain['all']")]
    public void Load_RejectsADefaultToolchainThatDoesNotExistOnThePlatformItServes(string key, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": { "platforms": ["linux"], "env": { "CC": "gcc" } } },
              "projects": [ { "name": "app", "type": "cmake", "defaultToolchain": { "{{key}}": "gcc" } } ],
              "legs": {
                "win": { "os": "windows", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist on 'windows'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <c>all</c> default serving only legs it can serve is fine: the key covers every platform,
    /// but only the ones some leg actually builds on have to be satisfiable.
    /// </summary>
    [Fact]
    public void Load_AcceptsAnAllDefaultToolchain_WhenEveryLegBuildingItIsOnAPlatformItExistsOn()
    {
        var config = LoadValid("""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": { "platforms": ["linux"], "env": { "CC": "gcc" } } },
              "projects": [ { "name": "app", "type": "cmake", "defaultToolchain": { "all": "gcc" } } ],
              "legs": {
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug" }
              }
            }
            """);

        Assert.Equal("gcc", Assert.Single(config.Projects).DefaultToolchain["all"]);
    }

    /// <summary>
    /// Every way a buildOutputs entry can be malformed is refused where the file is read, so the
    /// reader is told which line is wrong rather than handed a degenerate entry that witnesses
    /// nothing later.
    /// </summary>
    [Theory]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ 3 ] } ] }""", "a path, or a mapping")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ ["x"] ] } ] }""", "a path, or a mapping")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "windows": 3 } ] } ] }""", "is not a path")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ { "windows": { "x": "y" } } ] } ] }""", "is not a path")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ {} ] } ] }""", "names no path at all")]
    [InlineData("""{ "projects": [ { "name": "a", "type": "cmake", "buildOutputs": [ "" ] } ] }""", "empty path")]
    public void Load_RefusesAMalformedBuildOutput(string json, string expected)
    {
        var exception = LoadInvalid(json);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two spellings of one platform are one key on the entry, so the second would silently replace
    /// the first and a leg would be witnessed by a file nobody meant to name.
    /// </summary>
    [Fact]
    public void Load_RefusesABuildOutputThatNamesOnePlatformTwice()
    {
        var exception = LoadInvalid("""
            {
              "projects": [
                { "name": "a", "type": "cmake",
                  "buildOutputs": [ { "windows": "bin/a.exe", "WINDOWS": "bin/b.exe" } ] }
              ]
            }
            """);

        Assert.Contains("names platform 'WINDOWS' twice", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A keyed entry comes back keyed. Written back as a bare string it would lose every platform
    /// but one; written back as a map, an entry spelled as a string would turn every save into a
    /// diff against a file nobody edited.
    /// </summary>
    [Fact]
    public void ABuildOutput_RoundTripsInTheShapeItWasWritten()
    {
        using var temp = new TempDirectory();
        var store = CreateStore();
        var path = temp.Combine("config.json");

        store.Save(path, new HarnessConfig
        {
            Projects =
            {
                new ProjectConfig
                {
                    Name = "a",
                    Type = "cmake",
                    BuildOutputs =
                    [
                        "compile_commands.json",
                        BuildOutput.Keyed([
                            new KeyValuePair<string, string>("windows", "bin/a.exe"),
                            new KeyValuePair<string, string>("all", "bin/a"),
                        ]),
                    ],
                },
            },
        });

        var text = File.ReadAllText(path);

        // The plain entry stays plain, and the keyed one keeps every platform it named.
        Assert.Contains("\"compile_commands.json\"", text, StringComparison.Ordinal);
        Assert.Contains("\"windows\"", text, StringComparison.Ordinal);

        var outputs = Assert.Single(store.Load(path).Projects).BuildOutputs;

        Assert.Equal("compile_commands.json", outputs[0].Plain);
        Assert.Null(outputs[1].Plain);
        Assert.Equal("bin/a.exe", outputs[1].For("windows"));
        Assert.Equal("bin/a", outputs[1].For("linux"));
    }

    /// <summary>
    /// A list naming only platforms no leg runs on removes the tool from every host, which reads
    /// exactly like never having declared it — and the spelling that causes it is one mistyped word.
    /// </summary>
    [Fact]
    public void Load_RejectsAToolNeededOnlyWhereNoLegRuns()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "tools": [ { "name": "clang", "platforms": ["macos"] } ],
              "legs": { "nix": { "os": "linux", "processor": "x86_64", "config": "debug" } }
            }
            """);

        Assert.Contains("tool 'clang'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("never be checked anywhere", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool may be scoped narrower than an operating system: to toolchains, legs or leg sets,
    /// processors and emulators, each read as written.
    /// </summary>
    [Fact]
    public void ATool_MayNameTheToolchainsLegsProcessorsAndEmulatorsItIsNeededFor()
    {
        var tool = Assert.Single(LoadValid($$"""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "gcc": { "platforms": ["linux"], "env": { "CC": "gcc" } } },
              "emulators": { "qemu-arm64": {{QemuArm64}} },
              "legs": { "arm": { "os": "linux", "processor": "arm64", "config": "debug", "toolchain": "gcc", "emulator": "qemu-arm64" } },
              "legSets": { "gate": ["arm"] },
              "tools": [ { "name": "qemu-aarch64", "toolchains": ["gcc"], "legs": ["gate"], "processors": ["arm64"], "emulators": ["qemu-arm64"] } ]
            }
            """).Tools);

        Assert.Equal(["gcc"], tool.Toolchains);
        Assert.Equal(["gate"], tool.Legs);
        Assert.Equal(["arm64"], tool.Processors);
        Assert.Equal(["qemu-arm64"], tool.Emulators);
    }

    /// <summary>
    /// A scope naming nothing declared is refused naming it: read as no scope, the tool would be
    /// needed everywhere, and read as an empty one, nowhere.
    /// </summary>
    [Theory]
    [InlineData("\"toolchains\": [\"msvcc\"]", "tool 'cl' names toolchain 'msvcc', which is not declared under toolchains")]
    [InlineData("\"legs\": [\"wn\"]", "tool 'cl' names leg 'wn', which is neither a leg nor a leg set")]
    [InlineData("\"emulators\": [\"qemu\"]", "tool 'cl' names emulator 'qemu', which is not declared under emulators")]
    [InlineData("\"processors\": [\"aarch64\"]", "tool 'cl' names processor 'aarch64', which is not a processor; expected one of")]
    public void Load_RejectsAToolScopeThatNamesNothingDeclared(string scope, string expected)
    {
        var exception = LoadInvalid($$"""
            {
              "buildConfigs": { "debug": {} },
              "legs": { "win": { "os": "windows", "processor": "x86_64", "config": "debug" } },
              "tools": [ { "name": "cl", {{scope}} } ]
            }
            """);

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every scope must hold at once, so two that no one leg satisfies together cover nothing: the
    /// tool would never be checked anywhere, which reads exactly like never having declared it.
    /// </summary>
    [Fact]
    public void Load_RejectsAToolWhoseScopesTogetherCoverNoLeg()
    {
        var exception = LoadInvalid("""
            {
              "buildConfigs": { "debug": {} },
              "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" } }, "gcc": { "platforms": ["linux"], "env": { "CC": "gcc" } } },
              "legs": {
                "win": { "os": "windows", "processor": "x86_64", "config": "debug", "toolchain": "msvc" },
                "nix": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "gcc" }
              },
              "tools": [ { "name": "cl", "platforms": ["linux"], "toolchains": ["msvc"] } ]
            }
            """);

        Assert.Contains(
            "tool 'cl' is needed only by legs of platforms linux and toolchains msvc, and no declared leg is one, so it would never be checked anywhere",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A toolchain CMake builds with names the compiler it builds with - as a CMake project's default,
    /// or as a CMake leg's own. One naming none left CMake to take whatever it found first, which is how
    /// a leg named msvc built with MinGW's gcc on every run.
    /// </summary>
    [Theory]
    [InlineData("""{ "projects": [{ "name": "app", "type": "cmake", "path": ".", "defaultToolchain": { "linux": "gcc" } }] }""")]
    [InlineData("""{ "projects": [{ "name": "app", "type": "cmake", "path": "." }], "buildConfigs": { "debug": {} }, "legs": { "lin": { "os": "linux", "processor": "x86_64", "config": "debug", "toolchain": "gcc" } } }""")]
    public void Load_RejectsAToolchainCMakeBuildsWith_ThatNamesNoCompiler(string uses)
    {
        var exception = LoadInvalid(
            uses[..^1] + """, "toolchains": { "gcc": { "platforms": ["linux"], "generator": "Ninja", "env": { "CFLAGS": "-O2" } } } }""");

        Assert.Contains(
            "toolchain 'gcc', which CMake builds with, names no compiler: declare CC or CXX under its env, CMAKE_C_COMPILER, "
            + "CMAKE_CXX_COMPILER or CMAKE_TOOLCHAIN_FILE under its cacheVars, or the compilerId CMake must configure it with",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Any of these names a compiler for CMake: either language, in its environment or in the cache
    /// variable CMake is given for it - the one CMake itself reads first - a toolchain file, which
    /// names it for CMake, or the compilerId the build is held to.
    /// </summary>
    [Theory]
    [InlineData("\"env\": { \"CC\": \"gcc\" }")]
    [InlineData("\"env\": { \"CXX\": \"g++\" }")]
    [InlineData("\"cacheVars\": { \"CMAKE_C_COMPILER\": \"gcc\" }")]
    [InlineData("\"cacheVars\": { \"CMAKE_CXX_COMPILER\": \"g++\" }")]
    [InlineData("\"cacheVars\": { \"CMAKE_TOOLCHAIN_FILE\": \"cmake/arm-gcc.cmake\" }")]
    [InlineData("\"compilerId\": { \"C\": \"GNU\" }")]
    public void AToolchainNamingOneCompiler_AnyWay_IsAccepted(string names)
    {
        var config = LoadValid(
            $$"""{ "projects": [{ "name": "app", "type": "cmake", "path": ".", "defaultToolchain": { "linux": "gcc" } }], "toolchains": { "gcc": { "platforms": ["linux"], {{names}} } } }""");

        Assert.True(config.Toolchains.ContainsKey("gcc"));
    }

    /// <summary>
    /// A toolchain only a .NET or Dart project builds with names no compiler: their build systems
    /// resolve their own, and its cacheVars are that build's properties. Refused for it, a
    /// configuration that built was refused as a whole.
    /// </summary>
    [Fact]
    public void AToolchainOnlyANonCMakeProjectBuildsWith_NeedNameNoCompiler()
    {
        var config = LoadValid(
            """{ "projects": [{ "name": "app", "type": "dotnet", "path": "App.slnx", "defaultToolchain": { "all": "net" } }], "toolchains": { "net": { "platforms": ["all"], "cacheVars": { "TargetFramework": "net9.0" } } } }""");

        Assert.Equal("net9.0", config.Toolchains["net"].CacheVars["TargetFramework"]);
    }

    /// <summary>A toolchain may declare the compiler CMake must configure it with, by language.</summary>
    [Fact]
    public void AToolchain_MayDeclareTheCompilerCMakeMustConfigureItWith()
    {
        var config = LoadValid("""{ "toolchains": { "msvc": { "env": { "CC": "cl" }, "compilerId": { "C": "MSVC", "CXX": "MSVC" } } } }""");

        Assert.Equal("MSVC", config.Toolchains["msvc"].CompilerId["cxx"]);
    }

    /// <summary>
    /// A language CMake has no name like, or a language given no id, is refused: read as declared,
    /// it would never be answered, and every build would be unwitnessed over a typo.
    /// </summary>
    [Theory]
    [InlineData("\"C++\": \"MSVC\"", "toolchain 'msvc' compilerId names language 'C++', which is not a CMake language name such as C or CXX")]
    [InlineData("\"C\": \" \"", "toolchain 'msvc' compilerId gives 'C' no compiler id")]
    public void Load_RejectsACompilerIdThatNamesNoLanguageOrNoId(string entry, string expected)
    {
        var exception = LoadInvalid($$"""{ "toolchains": { "msvc": { "env": { "CC": "cl" }, "compilerId": { {{entry}} } } } }""");

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A toolchain may name the developer environment its legs start in, declared once under
    /// developerEnvironments; Visual Studio's asks for the C++ build tools unless it names another
    /// component.
    /// </summary>
    [Fact]
    public void AToolchain_MayNameTheDeveloperEnvironmentItsLegsStartIn()
    {
        var config = LoadValid(
            """{ "developerEnvironments": { "vs": { "kind": "visualStudio" } }, "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" }, "developerEnvironment": "vs" } } }""");

        Assert.Equal("vs", config.Toolchains["msvc"].DeveloperEnvironment);
        Assert.Equal(DeveloperEnvironmentKinds.VisualStudio, config.DeveloperEnvironments["VS"].Kind);
        Assert.Equal(DeveloperEnvironmentConfig.DefaultVisualStudioComponent, config.DeveloperEnvironments["vs"].RequiresComponent);
    }

    /// <summary>
    /// A developer environment this build cannot set up is refused when the file is read, and so is a
    /// toolchain naming one that is not declared, or Visual Studio's on a platform it never exists on:
    /// a leg there would be turned away on every run for want of it.
    /// </summary>
    [Theory]
    [InlineData(
        """{ "developerEnvironments": { "vs": { "kind": "xcode" } } }""",
        "developer environment 'vs' has kind 'xcode'; this build sets up visualStudio")]
    [InlineData(
        """{ "developerEnvironments": { "vs": { "kind": "visualStudio", "requiresComponent": " " } } }""",
        "developer environment 'vs' requiresComponent is blank; leave it out for the C++ build tools")]
    [InlineData(
        """{ "toolchains": { "msvc": { "platforms": ["windows"], "env": { "CC": "cl" }, "developerEnvironment": "vs" } } }""",
        "toolchain 'msvc' names developer environment 'vs', which is not declared under developerEnvironments")]
    [InlineData(
        """{ "developerEnvironments": { "vs": { "kind": "visualStudio" } }, "toolchains": { "msvc": { "platforms": ["windows", "linux"], "env": { "CC": "cl" }, "developerEnvironment": "vs" } } }""",
        "toolchain 'msvc' names developer environment 'vs', which Visual Studio sets up on windows alone, and declares platforms windows, linux; declare \"platforms\": [\"windows\"] for it")]
    [InlineData(
        """{ "developerEnvironments": { "vs": { "kind": "visualStudio" } }, "toolchains": { "msvc": { "env": { "CC": "cl" }, "developerEnvironment": "vs" } } }""",
        "toolchain 'msvc' names developer environment 'vs', which Visual Studio sets up on windows alone, and declares platforms all; declare \"platforms\": [\"windows\"] for it")]
    public void Load_RejectsADeveloperEnvironmentNoLegCouldStartIn(string json, string expected)
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

    /// <summary>
    /// A key this tool retired is refused like any unknown one, saying what took its place: told only
    /// that compilerCacheDirectory is unknown, a reader deletes the line and loses the per-host store
    /// it was there for.
    /// </summary>
    [Theory]
    [InlineData("""{ "hosts": { "local": { "compilerCacheDirectory": "/cache" } } }""", "hosts.local compilerCacheDirectory")]
    [InlineData("""{ "hosts": { "ssh": { "mac.mini": { "repositoryPath": "/r", "CompilerCacheDirectory": "/cache" } } } }""", "hosts.ssh 'mac.mini' compilerCacheDirectory")]
    [InlineData("""{ "hosts": { "wsl": { "Ubuntu": { "repositoryPath": "~/r", "compilerCacheDirectory": "/cache" } } } }""", "hosts.wsl 'Ubuntu' compilerCacheDirectory")]
    public void ARetiredKey_IsRefused_SayingWhatTookItsPlace(string json, string where)
    {
        var exception = LoadInvalid(json);

        Assert.Contains($"{where} is no longer read", exception.Message, StringComparison.Ordinal);
        Assert.Contains("\"CCACHE_DIR\"", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An unknown key that was never read is refused as unknown, with nothing said to take its place.</summary>
    [Fact]
    public void AnUnknownKey_IsNotMistakenForARetiredOne()
    {
        var exception = LoadInvalid("""{ "hosts": { "local": { "compilerCache": "/cache" } } }""");

        Assert.Contains("compilerCache", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no longer read", exception.Message, StringComparison.Ordinal);
    }

    private static ConfigException LoadInvalid(string json)
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("config.json", json);

        return Assert.Throws<ConfigException>(() => CreateStore().Load(path));
    }
}
