using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// A throwaway directory for tests that do real file work. Services that touch the
/// file system are exercised against a real one: an in-memory substitute would
/// prove they satisfy the substitute, not that they work.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    private static readonly IFileSystem Cleaner =
        new PhysicalFileSystem(FilePermissionsFactory.Create());

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(TestHost.TemporaryRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of the directory.</summary>
    public string Path { get; }

    /// <summary>Combines a relative path against this directory.</summary>
    public string Combine(params string[] parts)
        => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Writes a file, creating parent directories as needed.</summary>
    public string WriteFile(string relativePath, string contents = "")
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    /// <summary>
    /// Writes a program that would start, named <paramref name="name"/> in
    /// <paramref name="relativeDirectory"/>: with <c>.exe</c> on Windows, where a bare name starts
    /// nothing, and marked executable elsewhere.
    /// </summary>
    /// <returns>The program's whole path.</returns>
    public string WriteProgram(string relativeDirectory, string name, string contents = "#!/bin/sh\nexit 0\n")
    {
        var full = WriteFile(System.IO.Path.Combine(relativeDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name), contents);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(full, File.GetUnixFileMode(full) | UnixFileMode.UserExecute);
        }

        return full;
    }

    public void Dispose()
    {
        try
        {
            // The harness's own delete, which handles the read only files git leaves
            // behind. Plain Directory.Delete refuses those on Windows.
            Cleaner.DeleteDirectory(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not fail a test that passed, but a failure here is also a signal
            // about the delete code under test, so it is surfaced rather than swallowed.
            TestContext.Current.AddWarning($"Could not remove temporary directory '{Path}': {ex.Message}");
        }
    }
}
