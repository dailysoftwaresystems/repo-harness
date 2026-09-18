using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// What keeps two legs from writing into one build directory, and what refuses a directory that
/// belongs to another build.
/// </summary>
public sealed class BuildVariantTests
{
    [Fact]
    public void AnEmptySanitizer_IsTheSameVariantAsNoneAtAll()
    {
        // "sanitizer": "" is reachable from a hand-edited configuration. Left as two values they
        // are one build directory and two keys, which surfaces later as a shared-build-directory
        // refusal naming two legs their author believes are different.
        var declared = new VariantKey("x86_64", "msvc", "release", string.Empty);
        var absent = new VariantKey("x86_64", "msvc", "release", null);

        Assert.Equal(absent, declared);
        Assert.Equal(absent.GetHashCode(), declared.GetHashCode());
        Assert.Equal(absent.DirectoryName, declared.DirectoryName);
        Assert.Null(declared.Sanitizer);
    }

    [Fact]
    public void AVariantKey_NamesEveryThingThatChangesWhatIsCompiled()
    {
        Assert.Equal("x86_64-msvc-release", new VariantKey("x86_64", "msvc", "release", null).DirectoryName);
        Assert.Equal("arm64-gcc-debug-asan", new VariantKey("arm64", "gcc", "debug", "asan").DirectoryName);
    }

    [Fact]
    public void TwoToolchainsOverOneProject_NeverShareADirectory()
    {
        // CMake refuses a compiler change on an existing cache, so a shared directory is not a
        // performance question: one of the two legs simply cannot build.
        var msvc = new VariantKey("x86_64", "msvc", "release", null);
        var gcc = new VariantKey("x86_64", "gcc", "release", null);

        Assert.NotEqual(msvc.DirectoryName, gcc.DirectoryName);
    }

    [Fact]
    public void ABuildDirectory_IsAlwaysDerivedFromTheLegsOwnTreeRoot()
    {
        // A worktree's build output landing in the main checkout's build directory is how two legs
        // quietly read each other's objects.
        var variant = new VariantKey("x86_64", "gcc", "debug", null);

        PathAssert.Same(
            Path.Combine("/repo/.worktrees/lane", "build", "x86_64-gcc-debug"),
            variant.DirectoryUnder("/repo/.worktrees/lane"));
    }

    [Fact]
    public void ALegWithoutAToolchain_TakesTheProjectsDefaultForItsPlatform()
    {
        var config = new HarnessConfig
        {
            Projects =
            [
                new ProjectConfig
                {
                    Name = "main",
                    Type = "cmake",
                    DefaultToolchain = { ["windows"] = "msvc", ["linux"] = "gcc" },
                },
            ],
            Defaults = new HarnessDefaults { Project = "main" },
        };

        var leg = new LegConfig { Os = "linux", Processor = "x86_64", Config = "release" };

        Assert.Equal("gcc", VariantKey.For(config, leg, PlatformNames.Linux)?.Toolchain);
        Assert.Equal("msvc", VariantKey.For(config, leg, PlatformNames.Windows)?.Toolchain);
    }

    [Fact]
    public void ADirectoryConfiguredFromAnotherTree_IsRefused()
    {
        // Watching the wrong tree produced both a false refusal and a silent wrong answer: a build
        // that reported success having compiled a tree the caller had not touched.
        using var temp = new TempDirectory();
        var guard = Guard();
        var buildDirectory = WriteCache(temp, "CMAKE_HOME_DIRECTORY:INTERNAL=/repo/other");

        var refusal = Assert.Throws<HarnessException>(
            () => guard.Check(buildDirectory, "/repo/mine", expectedCompiler: null, expectedCxxCompiler: null, expectedBuildType: null));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("/repo/other", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryConfiguredForAnotherBuildType_IsRefusedRatherThanReconfigured()
    {
        // Reconfiguring leaves objects from two configurations in one directory, and nothing
        // afterwards says which one a binary came from.
        using var temp = new TempDirectory();
        var buildDirectory = WriteCache(temp, $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_BUILD_TYPE:STRING=Debug");

        var refusal = Assert.Throws<HarnessException>(
            () => Guard().Check(buildDirectory, temp.Path, expectedCompiler: null, expectedCxxCompiler: null, expectedBuildType: "Release"));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("fixed once per directory", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryConfiguredWithAnotherCompiler_IsRefused()
    {
        using var temp = new TempDirectory();
        var buildDirectory = WriteCache(
            temp,
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH=/usr/bin/gcc");

        var refusal = Assert.Throws<HarnessException>(
            () => Guard().Check(buildDirectory, temp.Path, expectedCompiler: CompilerValue.Read("clang"), expectedCxxCompiler: null, expectedBuildType: null));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
    }

    [Fact]
    public void TheSameCompilerSpeltAsAPathAndAsAName_IsNotARefusal()
    {
        // A cache records an absolute path and a leg names a program. Comparing them literally
        // would refuse every directory the harness itself configured.
        using var temp = new TempDirectory();
        var buildDirectory = WriteCache(
            temp,
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH=/usr/bin/gcc");

        Guard().Check(buildDirectory, temp.Path, expectedCompiler: CompilerValue.Read("gcc"), expectedCxxCompiler: null, expectedBuildType: null);
    }

    /// <summary>
    /// A compiler value that carries words after its program is compared as CMake recorded it: the
    /// program as CMAKE_C_COMPILER, the words as CMAKE_C_COMPILER_ARG1. The same value rebuilds, and a
    /// change in the words alone - ccache over clang, then over gcc - is refused, which CMake itself
    /// never notices.
    /// </summary>
    [Fact]
    public void ACompilerWithWordsAfterIt_IsComparedAsCMakeRecordedIt()
    {
        using var temp = new TempDirectory();
        var buildDirectory = WriteCache(
            temp,
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH=/usr/bin/ccache\nCMAKE_C_COMPILER_ARG1:STRING= clang");

        Guard().Check(buildDirectory, temp.Path, CompilerValue.Read("ccache clang"), null, null);

        var refusal = Assert.Throws<HarnessException>(
            () => Guard().Check(buildDirectory, temp.Path, CompilerValue.Read("ccache gcc"), null, null));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("configured with '/usr/bin/ccache clang', and this leg builds with 'ccache gcc'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryWithNoRecord_IsNotRefused()
    {
        // A first build has nothing to disagree with, and refusing here would make a clean tree
        // impossible to build.
        using var temp = new TempDirectory();

        Guard().Check(Path.Combine(temp.Path, "build", "x86_64-gcc-debug"), temp.Path, CompilerValue.Read("gcc"), CompilerValue.Read("g++"), "Debug");
    }

    /// <summary>
    /// A build directory records the program that builds it - the ninja the build ran - and the
    /// record is read back, so the dependency records are read by that same program.
    /// </summary>
    [Fact]
    public void ARecord_NamesTheProgramThatBuildsTheDirectory()
    {
        using var temp = new TempDirectory();
        var directory = WriteCache(temp, "CMAKE_MAKE_PROGRAM:FILEPATH=/opt/arm/bin/ninja\nCMAKE_BUILD_TYPE:STRING=Debug\n");

        Assert.Equal("/opt/arm/bin/ninja", Guard().Read(directory)?.MakeProgram);
    }

    private static BuildDirectoryGuard Guard()
        => new(new HarnessFactory().FileSystem, new HostPlatform());

    private static string WriteCache(TempDirectory temp, string contents)
    {
        var buildDirectory = Path.Combine(temp.Path, "build", "x86_64-gcc-debug");
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName), contents);

        return buildDirectory;
    }
}
