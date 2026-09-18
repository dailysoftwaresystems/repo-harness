using System.Runtime.CompilerServices;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// Process-wide setup for the test run, and the means to use this test assembly as a
/// child process whose behaviour a test controls exactly.
/// </summary>
internal static class TestHost
{
    /// <summary>Selects a child behaviour. Unset in an ordinary test run.</summary>
    internal const string ChildModeVariable = "REPO_HARNESS_TEST_CHILD";

    /// <summary>
    /// Root of every temporary directory the suite creates, with symbolic links resolved.
    /// </summary>
    /// <remarks>
    /// git reports resolved paths, so an expectation built from an unresolved directory
    /// fails for reasons unrelated to the code under test: macOS keeps its temporary
    /// directory under the /var link. GitHub's Windows runners report the system temporary
    /// directory through an 8.3 short name (RUNNER~1), which git reports in full, so
    /// RUNNER_TEMP, which has no short-name components, is preferred where it exists.
    /// Being short, it also helps the Windows path budget.
    /// </remarks>
    internal static string TemporaryRoot { get; } = CreateTemporaryRoot();

    /// <summary>The dotnet host running this suite, so children use the same runtime.</summary>
    internal static string DotnetExecutable { get; } = ResolveDotnet();

    /// <summary>This test assembly, which doubles as the child process.</summary>
    internal static string AssemblyPath => typeof(TestHost).Assembly.Location;

    [ModuleInitializer]
    internal static void Initialize()
    {
        var mode = Environment.GetEnvironmentVariable(ChildModeVariable);

        if (!string.IsNullOrEmpty(mode))
        {
            // Started by a test as a child: do what the mode asks, and exit before the test
            // framework ever starts.
            Environment.Exit(TestChild.Run(mode, [.. Environment.GetCommandLineArgs().Skip(1)]));
        }

        IsolateGitConfiguration();
    }

    /// <summary>
    /// Makes a program that really starts and exits zero, named <paramref name="name"/> in
    /// <paramref name="directory"/>, and returns its path.
    /// </summary>
    internal static string StartableProgram(string directory, string name)
    {
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            // A standalone system program, copied under a name no PATH can know.
            var copy = Path.Combine(directory, name + ".exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), copy);
            return copy;
        }

        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A request that runs this assembly as a child in <paramref name="mode"/>.</summary>
    internal static ProcessRequest ChildRequest(string mode, params string[] arguments) => new()
    {
        FileName = DotnetExecutable,
        Arguments = ["exec", AssemblyPath, .. arguments],
        Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ChildModeVariable] = mode,
        },
    };

    /// <summary>
    /// Points every git process the run starts at an empty configuration. A developer's
    /// global or system settings (commit signing, a global excludes file, core.autocrlf,
    /// hook paths) otherwise change what these tests observe, so a suite that passes on
    /// one machine fails on another for reasons unrelated to the code under test.
    /// </summary>
    private static void IsolateGitConfiguration()
    {
        var emptyConfiguration = Path.Combine(TemporaryRoot, "isolated.gitconfig");

        try
        {
            if (!File.Exists(emptyConfiguration))
            {
                File.WriteAllText(emptyConfiguration, string.Empty);
            }
        }
        catch (IOException)
        {
            // Another test process created it at the same moment: an empty file either way.
        }

        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", emptyConfiguration);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        // git searches upwards for a repository. Stopping it at the temporary root means a
        // test asserting "not a repository" cannot be answered by some repository that
        // happens to enclose the temporary directory on one machine.
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", TemporaryRoot);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runnerTemp
                ? runnerTemp
                : Path.GetTempPath(),
            "rh-test");

        Directory.CreateDirectory(root);
        return ResolveLinks(root);
    }

    /// <summary>Follows every symbolic link along <paramref name="path"/>, which must exist.</summary>
    private static string ResolveLinks(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var resolved = Path.GetPathRoot(full)!;

        foreach (var segment in full[resolved.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);

            if (new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                resolved = Path.TrimEndingDirectorySeparator(target.FullName);
            }
        }

        return resolved;
    }

    private static string ResolveDotnet()
    {
        var fromSdk = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromSdk) && File.Exists(fromSdk))
        {
            return fromSdk;
        }

        var current = Environment.ProcessPath;
        if (current is not null
            && string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return "dotnet";
    }
}
