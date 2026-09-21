namespace RepoHarness.Core.FileSystem;

/// <summary>
/// A directory of one use's own under the temporary directory, for files written only to hand to a
/// program and read back, and removed once that use is done.
/// </summary>
/// <remarks>
/// Made and written by whoever uses it, which says what failing to - a temporary directory this user
/// cannot write, a disk that is full - means for that use, naming <see cref="Path"/>. Removed on
/// <see cref="Dispose"/>, where one that cannot be - a scanner holding a file in it - is warned about
/// and fails nothing: what it held was already read, and the use it served has its answer.
/// </remarks>
public sealed class ScratchDirectory : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly Action<string> _warn;

    /// <summary>Names one; nothing is made until its user makes it.</summary>
    /// <param name="fileSystem">What it is removed through.</param>
    /// <param name="purpose">A word for what it holds, in its name: <c>vcvars</c>, <c>ignore</c>.</param>
    /// <param name="warn">Says, for the command using it, that it could not be removed, and why.</param>
    public ScratchDirectory(IFileSystem fileSystem, string purpose, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(warn);

        _fileSystem = fileSystem;
        _warn = warn;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dssharness-{purpose}-{Guid.NewGuid():N}");
    }

    /// <summary>Where it is: a name no other use shares, under the temporary directory.</summary>
    public string Path { get; }

    public void Dispose()
    {
        try
        {
            _fileSystem.DeleteDirectory(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warn($"'{Path}' could not be removed: {exception.Message}");
        }
    }
}
