using System.Security.Cryptography;
using System.Text;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Platform;

/// <summary>
/// The machine-wide lock two processes of this tool contend for.
/// </summary>
/// <remarks>
/// A named mutex is the lock .NET offers on Windows, Linux and macOS alike, and everything here that
/// must serialise a read-decide-write across processes takes one: the anchor registries, the run
/// lock, the log owner. One implementation, because the two things that go wrong with a mutex — a
/// name that two different subjects share, and an abandoned one read as a failure — go wrong the
/// same way wherever it is used, and a second copy would only fix one of them.
/// </remarks>
public static class MachineWideMutex
{
    /// <summary>
    /// The name one subject contends under, however its paths are spelled.
    /// </summary>
    /// <param name="prefix">What kind of subject this is, so two kinds never share a name.</param>
    /// <param name="paths">The paths the subject is, in any order.</param>
    /// <param name="ignoreCase">
    /// Whether the paths' case is part of their identity. False on a filesystem that distinguishes
    /// case; true elsewhere, where two spellings name one file and must therefore take one lock.
    /// </param>
    /// <remarks>
    /// Global, so a run started in another terminal session contends for the same subject rather
    /// than quietly getting a lock of its own.
    /// </remarks>
    public static string NameFor(string prefix, IEnumerable<string> paths, bool ignoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(paths);

        var key = string.Join('\n', paths
            .Select(Path.GetFullPath)
            .Select(path => ignoreCase ? path.ToUpperInvariant() : path)
            .Order(StringComparer.Ordinal));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        return $@"Global\repo-harness-{prefix}-" + Convert.ToHexStringLower(hash, 0, 16);
    }

    /// <summary>Opens the named mutex.</summary>
    /// <param name="name">The name, from <see cref="NameFor"/>.</param>
    /// <param name="subject">What is being locked, named in a refusal.</param>
    /// <exception cref="HarnessException">
    /// The mutex belongs to another user on this machine. Reported rather than worked around: the
    /// alternative is proceeding without the lock, which is the state the lock exists to prevent.
    /// </exception>
    public static Mutex Open(string name, string subject)
    {
        try
        {
            return new Mutex(initiallyOwned: false, name);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"The lock '{name}' on {subject} belongs to another user on this machine, so nothing was changed.",
                ex);
        }
    }

    /// <summary>Waits for the mutex, treating an abandoned one as taken.</summary>
    /// <param name="mutex">The mutex.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>Whether it was taken, and so must be released.</returns>
    /// <remarks>
    /// A previous holder that exited while holding it passes ownership to this thread, which is a
    /// success and not a failure: everything this lock protects is written by replacing a whole
    /// file, so nothing it left can be half written.
    /// </remarks>
    public static bool Wait(Mutex mutex, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(mutex);

        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
