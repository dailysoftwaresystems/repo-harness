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

    /// <summary>Checks <paramref name="emulator"/> on this machine.</summary>
    public async Task<EmulatorCheck> CheckAsync(EmulatorConfig emulator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emulator);

        if (!Same(emulator.HostOs, _platform.PlatformKey) || !Same(emulator.HostProcessor, _platform.Processor))
        {
            return EmulatorCheck.Unavailable(
                $"it runs on {emulator.HostOs} {emulator.HostProcessor} hosts, and this one is {_platform.PlatformKey} {_platform.Processor}");
        }

        if (emulator.Requires.FirstOrDefault(requirement => !IsPresent(requirement)) is { } missing)
        {
            return EmulatorCheck.Unavailable($"{missing} is missing");
        }

        string[] command = [.. emulator.Launcher ?? [], .. emulator.Witness.Command];
        ProcessResult result;

        try
        {
            result = await _processRunner.RunAsync(
                new ProcessRequest
                {
                    FileName = command[0],
                    Arguments = command[1..],
                    Environment = emulator.Env.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
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

    /// <summary>A requirement naming a path is checked where it points; any other is a program looked up on the PATH.</summary>
    private bool IsPresent(string requirement)
        => requirement.Contains('/') || requirement.Contains('\\')
            ? _fileSystem.FileExists(requirement) || _fileSystem.DirectoryExists(requirement)
            : _processRunner.FindExecutable(requirement) is not null;

    private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
