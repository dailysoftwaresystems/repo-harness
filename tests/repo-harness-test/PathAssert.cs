using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// Compares paths the way the platform does. An ordinal string assertion fails on Windows
/// for a path that differs only in the case of its drive letter, which names the same file.
/// </summary>
internal static class PathAssert
{
    private static readonly StringComparison Comparison = new HostPlatform().PathComparison;

    /// <summary>Whether two paths name the same location.</summary>
    internal static bool AreSame(string expected, string actual)
        => string.Equals(Normalize(expected), Normalize(actual), Comparison);

    /// <summary>Fails unless two paths name the same location.</summary>
    internal static void Same(string expected, string actual)
        => Assert.True(AreSame(expected, actual), $"Expected the path '{expected}' but found '{actual}'.");

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
