namespace RepoHarness.Core.FileSystem;

/// <summary>A file, and when it was last written.</summary>
/// <param name="Path">The file.</param>
/// <param name="LastWriteTimeUtc">When it was last written, in UTC.</param>
public sealed record WrittenFile(string Path, DateTime LastWriteTimeUtc)
{
    /// <summary>How many bytes it holds, where the walk that found it read that; zero where it was not asked.</summary>
    public long Length { get; init; }
}
