using System.Security.Cryptography;

namespace RepoHarness.Core.FileSystem;

/// <summary>
/// What a file holds, as the one identity every part of the harness compares by.
/// </summary>
/// <param name="Length">The file's length in bytes.</param>
/// <param name="Content">Lowercase hex SHA-256 of its bytes.</param>
/// <remarks>
/// Content, never a timestamp. One host this tool serves has a wall clock that steps forward by
/// about 25 seconds every few seconds, and the steps reach file modification times, so a file
/// written after a marker can carry a stamp from before it. Equality of content carries the same
/// distortion on both sides and a clock cannot bend it.
/// </remarks>
public readonly record struct FileContent(long Length, string Content);

/// <summary>Hashing a file's bytes.</summary>
/// <remarks>
/// One implementation, because two would eventually disagree about what "the same file" means and
/// the two things that compare files here — a tree sync deciding what to transfer, and a test run
/// deciding whether its inputs held still — would then answer differently about one file.
/// </remarks>
public static class FileContentHash
{
    /// <summary>Reads <paramref name="path"/> and returns its length and content hash.</summary>
    /// <param name="fileSystem">Opens the file, so a test can substitute what reading one does.</param>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <remarks>
    /// Read as bytes rather than as text: a hash of decoded text would call two files identical
    /// whenever they differ only in bytes no decoder keeps, and a corpus fixture is exactly such a
    /// file. Streamed rather than read whole, because a tree sync hashes every file in a repository.
    /// </remarks>
    public static async Task<FileContent> OfAsync(
        IFileSystem fileSystem,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        await using var stream = fileSystem.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return new FileContent(stream.Length, Convert.ToHexStringLower(hash));
    }

    /// <summary>The same hash, of bytes already in hand.</summary>
    /// <param name="contents">The bytes.</param>
    public static string Of(ReadOnlySpan<byte> contents) => Convert.ToHexStringLower(SHA256.HashData(contents));
}
