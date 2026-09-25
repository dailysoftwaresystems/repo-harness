using RepoHarness.Core.FileSystem;

namespace RepoHarness.Tests;

/// <summary>
/// The real file system, member for member, for a double to change what it needs to: every member is
/// virtual, so a double says only what it does differently.
/// </summary>
/// <param name="inner">The file system every member passes through to.</param>
internal class PassThroughFileSystem(IFileSystem inner) : IFileSystem
{
    public virtual bool FileExists(string path) => inner.FileExists(path);

    public virtual bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public virtual string ResolveLinks(string path) => inner.ResolveLinks(path);

    public virtual void CreateDirectory(string path) => inner.CreateDirectory(path);

    public virtual void DeleteFile(string path) => inner.DeleteFile(path);

    public virtual string CopyToTemporaryFile(string path) => inner.CopyToTemporaryFile(path);

    public virtual void CopyFile(string source, string destination, bool overwrite = false)
        => inner.CopyFile(source, destination, overwrite);

    public virtual void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public virtual void MoveDirectory(string source, string destination) => inner.MoveDirectory(source, destination);

    public virtual bool IsLink(string path) => inner.IsLink(path);

    public virtual long DirectorySize(string path) => inner.DirectorySize(path);

    public virtual DiskSpace SpaceAt(string path) => inner.SpaceAt(path);

    public virtual IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);

    public virtual IEnumerable<WrittenFile> EnumerateWrittenFiles(string path) => inner.EnumerateWrittenFiles(path);

    public virtual IEnumerable<string> EnumerateDirectoryLinks(string path) => inner.EnumerateDirectoryLinks(path);

    public virtual IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);

    public virtual string ReadAllText(string path) => inner.ReadAllText(path);

    public virtual Stream OpenRead(string path) => inner.OpenRead(path);

    public virtual DateTime LastWriteTimeUtc(string path) => inner.LastWriteTimeUtc(path);

    public virtual DateTime CreationTimeUtc(string path) => inner.CreationTimeUtc(path);

    public virtual Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        => inner.WriteAllBytesAtomicAsync(path, contents, cancellationToken);

    public virtual void WriteAllTextAtomic(string path, string contents) => inner.WriteAllTextAtomic(path, contents);

    public virtual void ProtectSecretFile(string path) => inner.ProtectSecretFile(path);
}
