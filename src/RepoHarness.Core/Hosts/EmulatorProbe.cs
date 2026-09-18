using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Checks, on the machine it runs on, whether an emulator can carry legs there: the machine is one it
/// runs on, what it requires is present, and its witness proves it runs programs for its processor.
/// </summary>
public sealed class EmulatorProbe(IHostPlatform platform, IProcessRunner processRunner, IFileSystem fileSystem)
{
    /// <summary>Longest a witness may take. It is a probe rather than a phase, so a time bound is right.</summary>
    public static readonly TimeSpan WitnessBudget = TimeSpan.FromSeconds(60);

    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>
    /// The programs <paramref name="emulator"/> needs found by name: what its requirements name, and
    /// the program its witness starts. What a search for them has to include.
    /// </summary>
    /// <param name="emulator">The emulator.</param>
    public static IEnumerable<string> ProgramsOf(EmulatorConfig emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        return WitnessCommand(emulator).Take(1)
            .Concat(emulator.Requires)
            .Where(program => !string.IsNullOrWhiteSpace(program) && !ProcessRunner.IsPath(program));
    }

    /// <summary>Checks <paramref name="emulator"/> on this machine.</summary>
    /// <param name="emulator">The emulator.</param>
    /// <param name="found">
    /// Where the search a leg here uses found this machine's programs, <see cref="ProgramsOf"/> among
    /// them. A requirement installed off the PATH is then present, and the witness is started with
    /// the PATH a leg's own run is given, rather than either being looked for on a PATH no leg uses.
    /// </param>
    /// <param name="cancellationToken">Stops the witness.</param>
    /// <exception cref="ArgumentException">
    /// A requirement named by name, or the program the witness starts, was not searched for.
    /// </exception>
    public async Task<EmulatorCheck> CheckAsync(
        EmulatorConfig emulator,
        ProgramSearch found,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        ArgumentNullException.ThrowIfNull(found);

        if (!Same(emulator.HostOs, _platform.PlatformKey) || !Same(emulator.HostProcessor, _platform.Processor))
        {
            return EmulatorCheck.Unavailable(
                $"it runs on {emulator.HostOs} {emulator.HostProcessor} hosts, and this one is {_platform.PlatformKey} {_platform.Processor}");
        }

        // A name is read from the search only where the witness is looked for on the PATH the search
        // saw. An emulator whose environment sets PATH is found on that PATH, which no search can
        // see: its programs are the witness's to find, and one the search missed is no missing one.
        var searched = !ProcessRunner.SetsPath(emulator.Env.Keys);

        foreach (var requirement in emulator.Requires.Where(requirement => searched || ProcessRunner.IsPath(requirement)))
        {
            if (Absence(requirement, found) is { } absent)
            {
                return EmulatorCheck.Unavailable(absent);
            }
        }

        var command = WitnessCommand(emulator);

        // The program the witness starts, when it is named rather than given by path, is read from
        // the search as a requirement is: one nobody could look for everywhere is unknown, and
        // starting it anyway would report it as not found.
        if (searched && !ProcessRunner.IsPath(command[0]) && Absence(command[0], found) is { } unstarted)
        {
            return EmulatorCheck.Unavailable(unstarted);
        }

        ProcessResult result;

        try
        {
            result = await _processRunner.RunAsync(
                new ProcessRequest
                {
                    FileName = command[0],
                    Arguments = command[1..],
                    Environment = emulator.Env.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
                    AppendToPath = found.Directories,
                    Timeout = WitnessBudget,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutableNotFoundException ex)
        {
            return EmulatorCheck.Unavailable($"{ex.FileName} was not found");
        }
        catch (ProgramStartException ex)
        {
            // A witness that exists but cannot start is the expected result on a host where the emulation is
            // not installed: a program for another processor with no handler registered for it. It says this
            // emulator cannot serve the host, not that the host, or the check, is broken.
            return EmulatorCheck.Unavailable($"its witness could not start: {ex.Message}");
        }

        if (result.TimedOut)
        {
            return EmulatorCheck.Unavailable($"its witness did not finish within {WitnessBudget.TotalSeconds:0} seconds");
        }

        if (result.ExitCode != 0)
        {
            return EmulatorCheck.Unavailable(
                $"its witness exited {result.ExitCode}: {HostProbes.Excerpt(result.StandardError + "\n" + result.StandardOutput)}");
        }

        var output = result.TrimmedOutput;

        try
        {
            return Regex.IsMatch(output, emulator.Witness.Pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1))
                ? new EmulatorCheck(true, null, HostProbes.Excerpt(output))
                : EmulatorCheck.Unavailable(
                    $"its witness printed '{HostProbes.Excerpt(output)}', which does not match {emulator.Witness.Pattern}");
        }
        catch (RegexMatchTimeoutException)
        {
            return EmulatorCheck.Unavailable($"its witness pattern {emulator.Witness.Pattern} took too long to match");
        }
    }

    /// <summary>The command the witness runs: the launcher, when there is one, and then the witness's own.</summary>
    private static string[] WitnessCommand(EmulatorConfig emulator) => [.. emulator.Launcher ?? [], .. emulator.Witness.Command];

    /// <summary>
    /// Why <paramref name="requirement"/> is not there, or <see langword="null"/> when it is. One
    /// naming a path is checked where it points, since a sysroot is a directory rather than a
    /// program; one naming a program is read from where the search found it.
    /// </summary>
    private string? Absence(string requirement, ProgramSearch found)
    {
        if (ProcessRunner.IsPath(requirement))
        {
            return _fileSystem.FileExists(requirement) || _fileSystem.DirectoryExists(requirement)
                ? null
                : $"{requirement} is missing";
        }

        if (!found.Found.TryGetValue(requirement, out var location))
        {
            throw new ArgumentException($"'{requirement}' was not among the programs searched for.", nameof(found));
        }

        return location.Found switch
        {
            ProgramFound.OnPath or ProgramFound.OffPath => null,
            ProgramFound.Unreadable => location.Unestablished(),
            _ => $"{requirement} is missing",
        };
    }

    private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
