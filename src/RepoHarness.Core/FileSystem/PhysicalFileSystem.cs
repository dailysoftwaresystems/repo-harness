using System.Text;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.FileSystem;

/// <inheritdoc cref="IFileSystem"/>
public sealed class PhysicalFileSystem(IFilePermissions filePermissions) : IFileSystem
{
    /// <summary>Attempts at the final rename before a transient refusal is reported.</summary>
    private const int ReplaceAttempts = 10;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IFilePermissions _filePermissions = filePermissions;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // git marks files under .git/objects read only, and a recursive delete
            // refuses those on Windows. Clearing the attribute is done only after the
            // delete actually refuses: doing it up front would walk the whole tree
            // twice on every delete, and on Unix would add write permission to every
            // file in a tree that would have deleted cleanly anyway.
            ClearReadOnlyAttributes(path);
            Directory.Delete(path, recursive: true);
        }
    }

    public IEnumerable<string> EnumerateFiles(string path, bool recursive)
        => Directory.EnumerateFiles(
            path,
            "*",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllTextAtomic(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            File.WriteAllText(temporary, contents, Utf8NoBom);
            ReplaceWith(temporary, path);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void ProtectSecretFile(string path) => _filePermissions.ProtectSecret(path);

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/> in a single
    /// operation that replaces any existing file.
    /// </summary>
    /// <remarks>
    /// Checking whether the destination exists and then choosing between a replace and
    /// a move races: two writers can both see it absent, and the second move throws.
    /// One overwriting rename has no such window. Windows can still refuse the rename
    /// for a moment while another process holds the destination, such as a concurrent
    /// writer or a virus scanner, so a refusal is retried a bounded number of times
    /// before it is reported.
    /// </remarks>
    private static void ReplaceWith(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < ReplaceAttempts)
            {
                Thread.Sleep(10 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < ReplaceAttempts)
            {
                Thread.Sleep(10 * attempt);
            }
        }
    }

    /// <summary>
    /// Removes a temporary file. A cleanup failure must never replace the failure that
    /// caused it: losing "there is not enough space on the disk" behind "cannot access
    /// the temporary file" sends the reader after the wrong problem entirely.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        // Reparse points are not followed: Directory.Delete removes the link rather
        // than its target, so clearing attributes through one would mutate files
        // outside the tree being deleted and then leave them there.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
