namespace RepoHarness.Core.FileSystem;

/// <summary>
/// File system access. A seam rather than a full abstraction: services that do
/// real file work are tested against real temporary directories, while services
/// that merely consult the file system are tested with a substitute.
/// </summary>
public interface IFileSystem
{
    /// <summary>Whether a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>Whether a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>Creates a directory and any missing parents. No-op when it exists.</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes a file. No-op when it is already absent.</summary>
    void DeleteFile(string path);

    /// <summary>
    /// Deletes a directory and everything under it, including files git has marked
    /// read only. No-op when absent.
    /// </summary>
    void DeleteDirectory(string path);

    /// <summary>Enumerates files under <paramref name="path"/>, recursively when asked.</summary>
    IEnumerable<string> EnumerateFiles(string path, bool recursive);

    /// <summary>Enumerates immediate subdirectories of <paramref name="path"/>.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>Reads a whole file as UTF-8 text.</summary>
    string ReadAllText(string path);

    /// <summary>
    /// Writes UTF-8 text, without a byte order mark, to a sibling temporary file and
    /// renames it over the target, so a reader never observes a half-written file and
    /// a crash never truncates one.
    /// </summary>
    void WriteAllTextAtomic(string path, string contents);

    /// <summary>
    /// Restricts a file to the current user: mode 0600 on Unix, which SSH requires of
    /// a private key, and an owner-only access list on Windows, with inherited
    /// permissions removed.
    /// </summary>
    void ProtectSecretFile(string path);
}
