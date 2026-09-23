namespace RepoHarness.Core.FileSystem;

/// <summary>A file, and when it was last written.</summary>
/// <param name="Path">The file.</param>
/// <param name="LastWriteTimeUtc">When it was last written, in UTC.</param>
public sealed record WrittenFile(string Path, DateTime LastWriteTimeUtc);
