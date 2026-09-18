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

    /// <summary>
    /// <paramref name="path"/> as an absolute path with every symbolic link and junction along it
    /// followed, the form git reports paths in. The part of the path that does not exist is kept
    /// as spelled.
    /// </summary>
    /// <exception cref="IOException">A link could not be read, or the links form a cycle.</exception>
    string ResolveLinks(string path);

    /// <summary>Creates a directory and any missing parents. No-op when it exists.</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes a file. No-op when it is already absent.</summary>
    void DeleteFile(string path);

    /// <summary>Copies a file to a new file in the temporary directory, and returns the copy's path.</summary>
    string CopyToTemporaryFile(string path);

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>.</summary>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">Where the copy goes. Its directory must exist.</param>
    /// <param name="overwrite">Whether an existing file there is replaced.</param>
    void CopyFile(string source, string destination, bool overwrite = false);

    /// <summary>
    /// Deletes a directory and everything under it, including files git has marked
    /// read only. No-op when absent.
    /// </summary>
    void DeleteDirectory(string path);

    /// <summary>Enumerates files under <paramref name="path"/>, recursively when asked.</summary>
    /// <remarks>
    /// A directory reached through a link or a junction is never walked: a link inside a tree leads
    /// out of it, or back into it and round again.
    /// </remarks>
    IEnumerable<string> EnumerateFiles(string path, bool recursive);

    /// <summary>Enumerates immediate subdirectories of <paramref name="path"/>.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>Reads a whole file as UTF-8 text.</summary>
    string ReadAllText(string path);

    /// <summary>
    /// Opens a file for reading its bytes. Streamed rather than read whole, because a tree sync
    /// hashes every file in a repository and holding one in memory per file bounds nothing.
    /// </summary>
    /// <param name="path">The file to open.</param>
    Stream OpenRead(string path);

    /// <summary>
    /// When <paramref name="path"/> was last written, in UTC.
    /// </summary>
    /// <param name="path">The file to ask about.</param>
    /// <exception cref="IOException">The file could not be asked about.</exception>
    /// <remarks>
    /// Behind the seam with everything else, and it raises rather than answering with a sentinel:
    /// the runtime returns the year 1601 for a path it cannot stat, and a caller comparing that
    /// against a build output concludes the file is older than everything and stops looking.
    /// </remarks>
    DateTime LastWriteTimeUtc(string path);

    /// <summary>
    /// Writes bytes to a sibling temporary file and renames it over the target, so a reader never
    /// observes a half-written file and an interruption never truncates one.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="contents">The bytes to write.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <remarks>
    /// A file whose content is unchanged is never rewritten by a caller, so its modification time
    /// does not move: an incremental build decides what is stale by ordering timestamps, and a sync
    /// that touched every file would either rebuild everything or, worse, leave a source looking
    /// older than the object built from it.
    /// </remarks>
    Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default);

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
