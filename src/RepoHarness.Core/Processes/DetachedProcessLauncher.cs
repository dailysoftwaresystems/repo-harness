using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace RepoHarness.Core.Processes;

/// <summary>Starts this tool again as a process of its own, which goes on after the one starting it has ended.</summary>
public interface IDetachedProcessLauncher
{
    /// <summary>Starts this tool with <paramref name="arguments"/>, detached: never waited for, and reading and writing nothing of this process.</summary>
    /// <param name="arguments">Its command line.</param>
    /// <exception cref="ProgramStartException">It could not be started.</exception>
    void StartSelf(IReadOnlyList<string> arguments);
}

/// <inheritdoc cref="IDetachedProcessLauncher"/>
/// <remarks>
/// The one process this tool starts and does not wait for. Its standard streams are its own, never this
/// process's: the connection a host serves a request over ends when every holder of its streams has closed
/// them, and a child holding them would keep the machine that asked waiting for as long as the child ran.
/// A command run over ssh has no terminal, so nothing is hung up on the child as the connection ends; on a
/// Windows host OpenSSH may still end the processes of a session with it.
/// </remarks>
public sealed class DetachedProcessLauncher : IDetachedProcessLauncher
{
    public void StartSelf(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var (program, prefix) = Self();
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Somewhere that stays: the directory a request was served in can be a copy removed later.
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var argument in prefix.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)
                ?? throw new ProgramStartException(program, $"'{program}' did not start.");

            // Its input ends at once: it reads none.
            process.StandardInput.Close();
        }
        catch (Win32Exception ex)
        {
            throw new ProgramStartException(program, $"'{program}' could not be started: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The program that is this tool, and what it is given before its own command line: nothing for the tool's
    /// own executable, and the tool's assembly for a runtime hosting it, as a build from source runs.
    /// </summary>
    private static (string Program, IReadOnlyList<string> Prefix) Self()
    {
        var program = Environment.ProcessPath ?? throw new ProgramStartException("dssharness", "this process does not know the program it runs as.");

        return string.Equals(Path.GetFileNameWithoutExtension(program), "dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } assembly
            ? (program, ["exec", assembly])
            : (program, []);
    }
}
