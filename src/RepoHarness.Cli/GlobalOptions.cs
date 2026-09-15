using System.CommandLine;

namespace RepoHarness.Cli;

/// <summary>Options every command accepts.</summary>
internal static class GlobalOptions
{
    /// <summary>Shows per-phase detail and echoes child process output.</summary>
    internal static Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Show detailed progress and child process output.",
    };

    /// <summary>
    /// Directory to act on, defaulting to the current one. Named after git's own
    /// option so the muscle memory transfers. It has no default value of its own:
    /// help would print the machine's current path as the default, so
    /// <see cref="CommandRunner"/> resolves an absent value instead.
    /// </summary>
    internal static Option<string> Directory { get; } = new("--directory", "-C")
    {
        Description = "Directory to operate in (default: current directory).",
    };

    /// <summary>Adds the global options to <paramref name="command"/>.</summary>
    internal static void AddTo(Command command)
    {
        command.Options.Add(Verbose);
        command.Options.Add(Directory);
    }
}
