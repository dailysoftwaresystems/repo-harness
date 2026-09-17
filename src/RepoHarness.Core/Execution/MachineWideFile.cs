using System.Globalization;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// Serialises one file's read-decide-write across every process on the machine.
/// </summary>
/// <remarks>
/// The run lock and the log owner are both decided by reading a file, deciding, and writing it
/// back. Two runs that each read "free" would each write themselves in, and both would believe they
/// held it. A named mutex is the machine-wide lock .NET offers on Windows, Linux and macOS alike,
/// and it is the same mechanism the anchor registries are changed under.
/// </remarks>
internal static class MachineWideFile
{
    /// <summary>
    /// Runs <paramref name="work"/> with no other process inside the same file's update.
    /// </summary>
    /// <remarks>
    /// The work is synchronous, and must stay so: a mutex is released by the thread that took it,
    /// and an await inside the work could resume on a different one.
    /// </remarks>
    /// <param name="path">The file being updated, which names the mutex.</param>
    /// <param name="window">
    /// How long to wait for another process's update. It bounds a rewrite that takes microseconds,
    /// never a run: what the file records is refused at once when it is already taken.
    /// </param>
    /// <param name="work">The read, the decision and the write.</param>
    /// <exception cref="HarnessException">Another process held the file for longer than the window allows.</exception>
    public static T Update<T>(string path, TimeSpan window, Func<T> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(work);

        var name = NameFor(path);
        using var mutex = MachineWideMutex.Open(name, $"'{path}'");
        var taken = Wait(mutex, path, window);

        try
        {
            return work();
        }
        finally
        {
            if (taken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// One mutex per file, however its path is spelled.
    /// </summary>
    /// <remarks>
    /// Case is always folded, whatever the filesystem says. On a case-sensitive one that can only
    /// make two genuinely different files share a lock, which costs a moment of waiting; the other
    /// way round, two spellings of one file would each believe they held it.
    /// </remarks>
    private static string NameFor(string path)
        => MachineWideMutex.NameFor("file", [path], ignoreCase: true);

    private static bool Wait(Mutex mutex, string path, TimeSpan window)
        => MachineWideMutex.Wait(mutex, window)
            ? true
            : throw new HarnessException(
                HarnessExit.Refused,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Another {ToolPackage.Id} process has been updating '{path}' for {window.TotalSeconds:0} seconds, so nothing was changed."));
}
