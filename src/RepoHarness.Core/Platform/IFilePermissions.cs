namespace RepoHarness.Core.Platform;

/// <summary>
/// File permission questions whose answers genuinely differ by platform: Unix
/// expresses them as mode bits, Windows as access lists and file extensions.
/// </summary>
public interface IFilePermissions
{
    /// <summary>
    /// Makes <paramref name="path"/> readable and writable only by the current user.
    /// SSH refuses to use a private key that anyone else can read.
    /// </summary>
    void ProtectSecret(string path);

    /// <summary>
    /// Whether <paramref name="path"/> is a file this platform would run. On Unix a file
    /// without an execute bit is not a program however it is named; on Windows that is
    /// decided by extension, which the caller has already applied.
    /// </summary>
    bool IsExecutable(string path);

    /// <summary>
    /// Whether <paramref name="path"/> is a file no other user can open: the condition ssh sets for
    /// a private key, which it ignores otherwise. A file that does not exist is not private.
    /// </summary>
    bool IsPrivate(string path);

    /// <summary>
    /// Whether <paramref name="path"/> is a file another user can change. ssh checks this itself only for
    /// its default configuration file, and reads one passed with <c>-F</c> whatever its permissions; such a
    /// file is refused anyway, because whoever can change it can make ssh run any command as the current
    /// user. A file that does not exist is not.
    /// </summary>
    bool IsWritableByOthers(string path);
}
