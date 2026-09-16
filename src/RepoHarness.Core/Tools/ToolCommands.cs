using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Tools;

/// <summary>
/// What a tool's <c>install</c> becomes: an argument list, never a shell line. A command whose first
/// word is <c>sudo</c> is run as a superuser, with the credential written to its standard input; every
/// other command runs as the user the host is reached as.
/// </summary>
/// <remarks>
/// Installing and updating are separate verbs for most package managers: <c>brew install</c> on a
/// package that is already there says so and changes nothing, which would report a tool as updated
/// while leaving it exactly as old as it was.
/// </remarks>
public static class ToolCommands
{
    /// <summary>
    /// The managers this build knows how to drive, named in a refusal so that a configuration using
    /// another one is told which ones it may use instead of its command being silently skipped.
    /// </summary>
    public static IReadOnlyList<string> KnownManagers { get; } =
        ["apt", "apt-get", "dnf", "yum", "apk", "zypper", "pacman", "brew", "winget", "choco", "scoop", "npm", "pip", "dotnet"];

    /// <summary>
    /// The command that installs or updates one tool, or <see langword="null"/> when the entry declares
    /// neither an explicit command nor a manager this build knows.
    /// </summary>
    /// <param name="install">The tool's install entry for this host's platform.</param>
    /// <param name="update">Whether the tool is there and behind, rather than absent.</param>
    public static IReadOnlyList<string>? For(ToolInstall install, bool update)
    {
        ArgumentNullException.ThrowIfNull(install);

        // An explicit command is the escape hatch for what no package manager expresses, so it is taken
        // exactly as written, for both verbs: whoever wrote it knows what it does to a tool already there.
        if (install.Command is { Count: > 0 } explicitCommand)
        {
            return [.. explicitCommand];
        }

        if (install.Manager is not { Length: > 0 } manager || install.Id is not { Length: > 0 } id)
        {
            return null;
        }

        return manager.ToLowerInvariant() switch
        {
            // apt, dnf, yum, apk, zypper and pacman each install into system directories, so each runs
            // under sudo; all six upgrade a package that is already installed with the same verb.
            "apt" or "apt-get" => ["sudo", "apt-get", "install", "-y", id],
            "dnf" => ["sudo", "dnf", "install", "-y", id],
            "yum" => ["sudo", "yum", "install", "-y", id],
            "apk" => ["sudo", "apk", "add", id],
            "zypper" => ["sudo", "zypper", "--non-interactive", "install", id],
            "pacman" => ["sudo", "pacman", "-S", "--noconfirm", id],

            // Homebrew refuses to run as root and installs into a directory its user owns.
            "brew" => update ? ["brew", "upgrade", id] : ["brew", "install", id],
            "winget" => update
                ? ["winget", "upgrade", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--id", id]
                : ["winget", "install", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--id", id],
            "choco" => update ? ["choco", "upgrade", "-y", id] : ["choco", "install", "-y", id],
            "scoop" => update ? ["scoop", "update", id] : ["scoop", "install", id],
            "npm" => ["npm", "install", "--global", id],
            "pip" or "pip3" => ["pip3", "install", "--user", "--upgrade", id],
            "dotnet" => update
                ? ["dotnet", "tool", "update", "--global", id]
                : ["dotnet", "tool", "install", "--global", id],
            _ => null,
        };
    }
}
