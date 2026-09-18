using System.Collections.Immutable;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Where a program is looked for when the PATH of a command run without a login shell does not
/// name it.
/// </summary>
/// <remarks>
/// A login shell's PATH is not the PATH a command sees. Measured: on macOS <c>/opt/homebrew/bin</c>
/// is absent from the PATH of a command run over ssh, and in a WSL distribution <c>~/.dotnet</c> is
/// absent from the PATH of a command run with <c>wsl.exe --exec</c>. A login shell is not the
/// answer either: a profile that replaces PATH was measured making <c>command -v</c> report the
/// wrong thing over ssh, which is why these directories are looked in on the file system instead.
/// <para>
/// One list for every search, because two is how a survey came to call a leg runnable whose run then
/// could not start its build tool: the search that knew about <c>/opt/homebrew/bin</c> was used only
/// by <c>install-missing-tools</c>, and the run looked on its PATH and nowhere else. The survey, the
/// run, and <c>install-missing-tools</c> over a connection all read this.
/// </para>
/// </remarks>
public static class ToolSearchDirectories
{
    /// <summary>
    /// The directories searched on a POSIX host, in the order they are preferred. <c>~</c> is the
    /// home directory of whoever is searching.
    /// </summary>
    public static ImmutableArray<string> Posix { get; } =
    [
        // Where dotnet-install.sh puts the SDK, and where install-missing-tools therefore puts it.
        "~/.dotnet",
        "~/.local/bin",
        "~/bin",

        // Homebrew on Apple silicon, absent from an ssh command's PATH; then Homebrew on Intel, which
        // is also where a great many installers put a program.
        "/opt/homebrew/bin",
        "/usr/local/bin",

        // The .NET installers' own directories, per operating system and packaging.
        "/usr/local/share/dotnet",
        "/usr/share/dotnet",
        "/usr/lib/dotnet",
        "/opt/dotnet",
        "/snap/bin",
    ];

    /// <summary>The directories searched on <paramref name="platformKey"/> when nothing is declared for it.</summary>
    /// <param name="platformKey">The platform being searched.</param>
    /// <remarks>
    /// None on Windows: an installer there puts a program on the machine PATH, which a command run
    /// without a login shell already carries, and none of these directories exist there.
    /// </remarks>
    public static IReadOnlyList<string> BuiltIn(string? platformKey)
        => string.Equals(platformKey, PlatformNames.Windows, StringComparison.OrdinalIgnoreCase) ? [] : Posix;

    /// <summary>
    /// The directories to search on <paramref name="platformKey"/>: the list configuration declares
    /// for it, when that list has something in it, and the built-in list otherwise.
    /// </summary>
    /// <param name="declared"><c>toolSearchDirectories</c>, by platform or <c>all</c>.</param>
    /// <param name="platformKey">The platform being searched.</param>
    /// <remarks>
    /// A declared list replaces the built-in one rather than adding to it, so the file says exactly
    /// where is searched. Declared but empty is not a replacement: an empty list would mean "search
    /// nothing", which is never what somebody writing the key meant, and it would turn every program
    /// off the PATH into one that is missing.
    /// </remarks>
    public static IReadOnlyList<string> For(IReadOnlyDictionary<string, List<string>>? declared, string? platformKey)
        => PlatformScope.Select(declared, platformKey) is { Count: > 0 } chosen ? chosen : BuiltIn(platformKey);

    /// <summary>
    /// The entries of <paramref name="directories"/> that name a directory on a
    /// <paramref name="platformKey"/> machine. The rest are another platform's - <c>/opt/tools</c>
    /// under <c>all</c>, for a Windows host - and are not looked for there.
    /// </summary>
    /// <param name="directories">The entries chosen for the platform.</param>
    /// <param name="platformKey">The platform searching, as measured on the host.</param>
    /// <remarks>
    /// For a search made from another machine, which has only the text to go on. A search on the host
    /// itself asks that machine, which reads the entries the same way.
    /// </remarks>
    public static IReadOnlyList<string> On(IReadOnlyList<string> directories, string? platformKey)
    {
        ArgumentNullException.ThrowIfNull(directories);

        return [.. directories.Where(directory => Names(directory, platformKey))];
    }

    /// <summary>
    /// Whether <paramref name="directory"/> names one directory on a <paramref name="platformKey"/>
    /// machine wherever a search there starts: <c>~/</c> for the home directory of whoever searches,
    /// or a path that platform reads as absolute - a drive or a share on Windows, a leading <c>/</c>
    /// everywhere else.
    /// </summary>
    /// <param name="directory">An entry of <c>toolSearchDirectories</c>.</param>
    /// <param name="platformKey">The platform searching.</param>
    /// <remarks>
    /// Read from the text rather than by asking this machine, so a configuration is judged the same
    /// wherever it is read - including entries meant for a platform this machine is not. A search
    /// asks the machine it runs on instead, which reads the same entries the same way:
    /// <c>/opt/tools</c> under <c>all</c> is searched on macOS and Linux, and on Windows, where it
    /// would be read against whichever drive the search started on, it is passed over.
    /// </remarks>
    public static bool Names(string directory, string? platformKey)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (directory.StartsWith("~/", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(platformKey, PlatformNames.Windows, StringComparison.OrdinalIgnoreCase))
        {
            return directory.StartsWith('/');
        }

        var drive = directory.Length >= 3
            && char.IsAsciiLetter(directory[0])
            && directory[1] == ':'
            && directory[2] is '\\' or '/';

        return drive || directory.StartsWith(@"\\", StringComparison.Ordinal) || directory.StartsWith("//", StringComparison.Ordinal);
    }
}
