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
    /// <summary>How many times opening a mutex is tried before it is given up on.</summary>
    internal const int Attempts = 5;

    /// <summary>How long the first retry waits; each after it waits that much longer than the one before.</summary>
    internal static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(25);

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
    /// The mutex belongs to another user on this machine, or could not be opened in <see cref="Attempts"/>
    /// tries. Reported rather than worked around: the alternative is proceeding without the lock, which is
    /// the state the lock exists to prevent.
    /// </exception>
    /// <remarks>
    /// An open the system failed with an I/O error is tried again, <see cref="Attempts"/> times in all and a
    /// little later each time. The one measured is a race: on Linux and macOS the runtime keeps a named
    /// mutex under a directory it creates the first time one is opened, and processes opening theirs at
    /// that moment race to create it. In WSL, 16 processes started together on a fresh
    /// <c>/tmp/.dotnet</c>, twenty times over, failed 54 and then 66 of their 320 openings, the runtime
    /// saying a stat of the directory found nothing - a consumer saw its mkdir find one already there -
    /// and none with the retry: the loser finds the directory the next time it looks. Without it, the leg
    /// whose run lost the race ended with an internal error, reported as a host that could not be reached.
    /// An error that lasts - a full disk, a directory nobody may write - costs the tries, a quarter of a
    /// second, and is then said as unavailable rather than as a refusal: this machine cannot run anything
    /// until it opens, and a machine running a leg for another is one host of many, whose trouble is its own.
    /// </remarks>
    public static Mutex Open(string name, string subject)
        => Open(name, subject, () => new Mutex(initiallyOwned: false, name), Thread.Sleep);

    /// <summary>Opens a mutex through <paramref name="create"/>, pausing through <paramref name="pause"/> between tries.</summary>
    /// <param name="name">The name, from <see cref="NameFor"/>.</param>
    /// <param name="subject">What is being locked, named in a refusal.</param>
    /// <param name="create">Opens the mutex.</param>
    /// <param name="pause">Waits between two tries.</param>
    /// <exception cref="HarnessException">The mutex belongs to another user, or could not be opened in <see cref="Attempts"/> tries.</exception>
    internal static Mutex Open(string name, string subject, Func<Mutex> create, Action<TimeSpan> pause)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(pause);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return create();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"The lock '{name}' on {subject} belongs to another user on this machine, so nothing was changed.",
                    ex);
            }
            catch (IOException) when (attempt < Attempts)
            {
                pause(Backoff * attempt);
            }
            catch (IOException ex)
            {
                throw new HarnessException(
                    HarnessExit.HostUnavailable,
                    $"The lock '{name}' on {subject} could not be opened in {Attempts} tries, the last saying: "
                    + $"{ex.Message.TrimEnd('.')}. Nothing was changed, and nothing can run on this machine until "
                    + "it opens: fix what the system said, and run the command again.",
                    ex);
            }
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
