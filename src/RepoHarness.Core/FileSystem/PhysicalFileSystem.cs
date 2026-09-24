using System.IO.Enumeration;
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

    public string ResolveLinks(string path)
    {
        // Followed as realpath follows them: after each link the walk starts again from the root,
        // so a link within a link's target is followed too, and a cycle of links ends the walk.
        const int MaxLinks = 40;
        var pending = Path.GetFullPath(path);

        for (var links = 0; links <= MaxLinks; links++)
        {
            var root = Path.GetPathRoot(pending) ?? string.Empty;
            var segments = pending[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var resolved = root;
            string? restart = null;

            for (var index = 0; index < segments.Length && restart is null; index++)
            {
                var next = Path.Combine(resolved, segments[index]);
                var directory = new DirectoryInfo(next);

                if (directory.LinkTarget is { } target)
                {
                    restart = Path.Combine([Path.Combine(resolved, target), .. segments[(index + 1)..]]);
                }
                else if (!directory.Exists)
                {
                    // Nothing below a directory that does not exist can be a link.
                    return Path.TrimEndingDirectorySeparator(Path.Combine([next, .. segments[(index + 1)..]]));
                }
                else
                {
                    resolved = next;
                }
            }

            if (restart is null)
            {
                return Path.TrimEndingDirectorySeparator(resolved);
            }

            pending = Path.GetFullPath(restart);
        }

        throw new IOException($"More than {MaxLinks} links along '{path}', which may form a cycle.");
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public string CopyToTemporaryFile(string path)
    {
        var copy = Path.Combine(Path.GetTempPath(), "dssharness-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.Copy(path, copy);
        }
        catch
        {
            // A copy that failed part way must not stay behind as a partial file.
            TryDelete(copy);
            throw;
        }

        return copy;
    }

    public void CopyFile(string source, string destination, bool overwrite = false)
        => File.Copy(source, destination, overwrite);

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
        => Walk(path, recursive, (ref FileSystemEntry entry) => !entry.IsDirectory, (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath());

    public IEnumerable<WrittenFile> EnumerateWrittenFiles(string path)
        => Dated(Walk(
            path,
            recursive: true,
            (ref FileSystemEntry entry) => !entry.IsDirectory,
            (ref FileSystemEntry entry) => new WrittenFile(entry.ToSpecifiedFullPath(), entry.LastWriteTimeUtc.UtcDateTime)));

    /// <summary>
    /// <paramref name="walked"/>, each dated by the walk's own reading of its time where the walk had one,
    /// and by asking it again directly where it had none; a file gone since the walk listed it is left out.
    /// </summary>
    /// <param name="walked">The files a walk listed, with the times it read for them.</param>
    /// <remarks>
    /// The walk's own reading costs nothing more on Windows. Where the runtime could not stat an entry it
    /// answers 1601, and the file is asked again directly. On Linux and macOS a walk lists a directory
    /// before it stats what is in it, so a file something else removes in between - a compiler's
    /// temporary, a test's scratch file - is one it could not stat, and asked again it is not there. It is
    /// no longer in the directory at all, and the walk describes what is: raised, one file deleted at the
    /// wrong moment left a build nothing to date its changes against. One that is there, and whose time
    /// still cannot be read, raises what kept it.
    /// </remarks>
    internal IEnumerable<WrittenFile> Dated(IEnumerable<WrittenFile> walked)
    {
        ArgumentNullException.ThrowIfNull(walked);

        foreach (var file in walked)
        {
            if (file.LastWriteTimeUtc != Unstatted)
            {
                yield return file;
                continue;
            }

            DateTime written;

            try
            {
                written = LastWriteTimeUtc(file.Path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            yield return file with { LastWriteTimeUtc = written };
        }
    }

    public IEnumerable<string> EnumerateDirectoryLinks(string path)
        => Walk(path, recursive: true, (ref FileSystemEntry entry) => entry.IsDirectory && IsLink(ref entry), (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath());

    /// <summary>The time the runtime gives an entry it could not stat.</summary>
    private static readonly DateTime Unstatted = DateTime.FromFileTimeUtc(0);

    /// <summary>
    /// The entries under <paramref name="path"/> that <paramref name="include"/> accepts, as
    /// <paramref name="transform"/> reads each, never walking a directory reached through a link.
    /// </summary>
    /// <remarks>
    /// What the plain overload did - nothing skipped, and a directory that cannot be read said so -
    /// with one difference: a directory reached through a link or a junction is never walked. A link
    /// inside a tree leads either out of it, to files that tree does not contain, or back into it,
    /// which a walk follows until the stack goes. One walk for the files and for the links it passed
    /// over, so the links a caller is told about are exactly the directories it did not see.
    /// </remarks>
    private static FileSystemEnumerable<T> Walk<T>(
        string path,
        bool recursive,
        FileSystemEnumerable<T>.FindPredicate include,
        FileSystemEnumerable<T>.FindTransform transform)
        => new(
            path,
            transform,
            new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                MatchType = MatchType.Win32,
            })
        {
            ShouldIncludePredicate = include,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsLink(ref entry),
        };

    private static bool IsLink(ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;

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

    public DateTime LastWriteTimeUtc(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);

        // Refreshed, so it answers for now; Refresh() itself raises nothing. A path with nothing at it, a
        // directory, and a file the runtime could not look up all read as no file there. Asked for its
        // time, the runtime then raises what kept it from looking the last up - measured on Linux, a file
        // in a directory that can be listed but not searched raised UnauthorizedAccessException - and
        // answers for the others: 1601-01-01 for nothing, which reads as a real time, and a directory's
        // own time, which is no file's. Those two are raised here as not found.
        info.Refresh();

        if (info.Exists)
        {
            return info.LastWriteTimeUtc;
        }

        _ = info.LastWriteTimeUtc;

        throw new FileNotFoundException($"'{path}' is not there to be asked when it was last written.", path);
    }

    public DateTime CreationTimeUtc(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);

        // Asked as LastWriteTimeUtc asks, so it raises what that raises rather than answering 1601.
        info.Refresh();

        if (info.Exists)
        {
            return info.CreationTimeUtc;
        }

        _ = info.CreationTimeUtc;

        throw new FileNotFoundException($"'{path}' is not there to be asked when it was created.", path);
    }

    public Stream OpenRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Sequential, and sharing read access: a tree sync walks every file in a repository while
        // other tools legitimately hold some of them open for reading.
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            });
    }

    public async Task WriteAllBytesAtomicAsync(
        string path,
        byte[] contents,
        CancellationToken cancellationToken = default)
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
            await File.WriteAllBytesAsync(temporary, contents, cancellationToken).ConfigureAwait(false);
            await ReplaceWithAsync(temporary, path, cancellationToken).ConfigureAwait(false);
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
    /// <see cref="ReplaceWith"/> without blocking the thread between attempts, and stopping when
    /// the caller does.
    /// </summary>
    /// <remarks>
    /// The synchronous form blocks a thread-pool thread for up to 450ms per file while it waits.
    /// A tree sync writes thousands of files, and the parallel legs above it are sharing that pool:
    /// sleeping in it makes a transient lock on one file look like a stall in every other leg.
    /// </remarks>
    private static async Task ReplaceWithAsync(string source, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReplaceAttempts)
            {
                await Task.Delay(10 * attempt, cancellationToken).ConfigureAwait(false);
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
