using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Build;

/// <summary>What an existing build directory was configured for.</summary>
/// <param name="HomeDirectory">The source tree it was configured from, or null when it did not say.</param>
/// <param name="CCompiler">The C compiler recorded in it, or null.</param>
/// <param name="CxxCompiler">The C++ compiler recorded in it, or null.</param>
/// <param name="BuildType">The build type recorded in it, or null.</param>
public sealed record BuildDirectoryRecord(
    string? HomeDirectory,
    string? CCompiler,
    string? CxxCompiler,
    string? BuildType);

/// <summary>
/// Refuses a build directory that was configured for something other than this leg.
/// </summary>
/// <remarks>
/// Reusing one is not a performance question. A build directory records the tree it was configured
/// from, and a directory configured from another worktree watches that tree's sources: it produced
/// both a refusal nobody could explain and, worse, a silent wrong answer, where a build reported
/// success having compiled a tree the caller had not touched. The build type is checked for the same
/// reason as the compiler: silently reconfiguring it turns an incremental directory into a mix of
/// objects from two configurations, and nothing afterwards says which one a binary came from.
/// </remarks>
public sealed class BuildDirectoryGuard(IFileSystem fileSystem, IHostPlatform platform)
{
    /// <summary>The file a CMake build directory records its configuration in.</summary>
    public const string CMakeCacheFileName = "CMakeCache.txt";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <summary>Reads what a build directory was configured for, or null when it holds no record.</summary>
    /// <param name="buildDirectory">The directory to read.</param>
    public BuildDirectoryRecord? Read(string buildDirectory)
    {
        var cache = Path.Combine(buildDirectory, CMakeCacheFileName);

        if (!_fileSystem.FileExists(cache))
        {
            return null;
        }

        string? home = null;
        string? cCompiler = null;
        string? cxxCompiler = null;
        string? buildType = null;

        foreach (var line in _fileSystem.ReadAllText(cache).Split('\n'))
        {
            var text = line.Trim();

            home ??= ValueOf(text, "CMAKE_HOME_DIRECTORY");
            cCompiler ??= ValueOf(text, "CMAKE_C_COMPILER");
            cxxCompiler ??= ValueOf(text, "CMAKE_CXX_COMPILER");
            buildType ??= ValueOf(text, "CMAKE_BUILD_TYPE");
        }

        return new BuildDirectoryRecord(home, cCompiler, cxxCompiler, buildType);
    }

    /// <summary>
    /// Refuses when the directory was configured from another tree, with another compiler, or for
    /// another build type.
    /// </summary>
    /// <param name="buildDirectory">The directory this leg would build in.</param>
    /// <param name="treeRoot">The tree this leg acts on.</param>
    /// <param name="expectedCompiler">The compiler this leg builds with, or null when it names none.</param>
    /// <param name="expectedBuildType">The build type this leg builds, or null when it names none.</param>
    /// <exception cref="HarnessException">The directory belongs to a different build.</exception>
    public void Check(
        string buildDirectory,
        string treeRoot,
        string? expectedCompiler,
        string? expectedBuildType)
    {
        var record = Read(buildDirectory);

        if (record is null)
        {
            return;
        }

        if (record.HomeDirectory is { Length: > 0 } home && !SamePath(home, treeRoot))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' was configured from '{home}', not from '{treeRoot}'. Building in it "
                + "would compile that tree's sources and report on this one. Delete it, or point this leg "
                + "at its own build directory.");
        }

        if (expectedCompiler is { Length: > 0 }
            && record.CCompiler is { Length: > 0 } recordedCompiler
            && !NamesSameProgram(recordedCompiler, expectedCompiler))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' was configured with '{recordedCompiler}', and this leg builds with "
                + $"'{expectedCompiler}'. A build system refuses that change on an existing cache; "
                + "delete the directory rather than reconfiguring it.");
        }

        if (expectedBuildType is { Length: > 0 }
            && record.BuildType is { Length: > 0 } recordedType
            && !string.Equals(recordedType, expectedBuildType, StringComparison.OrdinalIgnoreCase))
        {
            // Refused rather than reconfigured. Reconfiguring leaves objects from both types in one
            // directory, and nothing afterwards says which type a binary was built with.
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' was configured as '{recordedType}', and this leg builds "
                + $"'{expectedBuildType}'. A build type is fixed once per directory: delete it, or give "
                + "this leg a build directory of its own.");
        }
    }

    private bool SamePath(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Replace('/', Path.DirectorySeparatorChar))),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            _platform.PathComparison);

    /// <summary>
    /// Whether two recorded compilers name the same program. Compared by file name without its
    /// extension, because a cache records an absolute path and a leg names a program.
    /// </summary>
    private bool NamesSameProgram(string recorded, string expected)
    {
        if (string.Equals(recorded, expected, _platform.PathComparison))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(recorded),
            Path.GetFileNameWithoutExtension(expected),
            _platform.PathComparison);
    }

    private static string? ValueOf(string line, string name)
    {
        // CMakeCache.txt lines are NAME:TYPE=VALUE. The type is not part of the name, and a line
        // whose name merely starts with this one is a different variable.
        if (!line.StartsWith(name, StringComparison.Ordinal) || line.Length <= name.Length)
        {
            return null;
        }

        var rest = line[name.Length..];

        if (rest[0] is not (':' or '='))
        {
            return null;
        }

        var equals = rest.IndexOf('=', StringComparison.Ordinal);

        return equals < 0 ? null : rest[(equals + 1)..].Trim();
    }
}
