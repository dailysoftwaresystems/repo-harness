namespace RepoHarness.Core.Platform;

/// <summary>
/// How a path reads on a platform, decided from its text alone.
/// </summary>
/// <remarks>
/// From the text rather than by asking this machine, so a configuration is judged the same wherever
/// it is read - including a path meant for a platform this machine is not. The machine that uses a
/// path reads the same text the same way, so the two cannot disagree about it.
/// </remarks>
public static class PlatformPaths
{
    /// <summary>
    /// Whether <paramref name="path"/> is absolute on a <paramref name="platformKey"/> machine: a
    /// drive or a share on Windows, a leading <c>/</c> everywhere else.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    /// <param name="platformKey">The platform that reads it.</param>
    public static bool IsAbsoluteOn(string path, string? platformKey)
    {
        ArgumentNullException.ThrowIfNull(path);

        return string.Equals(platformKey, PlatformNames.Windows, StringComparison.OrdinalIgnoreCase)
            ? IsWindowsAbsolute(path)
            : path.StartsWith('/');
    }

    /// <summary>
    /// Whether <paramref name="path"/> is absolute on some platform: a leading <c>/</c>, or a drive
    /// or a share. What a setting read on any machine, for a machine it cannot tell, may be.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    public static bool IsAbsoluteOnAnyPlatform(string path)
        => IsAbsoluteOn(path, PlatformNames.Linux) || IsAbsoluteOn(path, PlatformNames.Windows);

    /// <summary>
    /// Whether <paramref name="path"/> starts from the home directory of whoever reads it: <c>~/</c>,
    /// which each machine expands against its own.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    public static bool IsHomeRelative(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.StartsWith("~/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is rooted at a drive - <c>C:\</c> or <c>C:/</c> - or names a
    /// share: what Windows reads as absolute.
    /// </summary>
    /// <param name="path">The path as a configuration spells it.</param>
    /// <remarks>
    /// <c>C:tools</c> is not: it is relative to whatever directory that drive was last in.
    /// </remarks>
    public static bool IsWindowsAbsolute(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var drive = path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] is '\\' or '/';

        return drive || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="path"/> starts with a drive letter, as <c>C:tools</c> and
    /// <c>C:\tools</c> both do. Windows reads either as naming a place on that drive.
    /// </summary>
    /// <param name="path">The text to read.</param>
    public static bool NamesADrive(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    }
}
