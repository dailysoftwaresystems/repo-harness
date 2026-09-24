using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>An SDK as <c>dotnet --list-sdks</c> lists it.</summary>
/// <param name="Version">The SDK's version.</param>
/// <param name="Location">Where it is installed.</param>
public sealed record SdkListing(string Version, string Location)
{
    /// <summary>The SDK's major version, or 0 when the version cannot be read.</summary>
    public int Major => int.TryParse(Version.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
        ? major
        : 0;

    /// <summary>
    /// Whether it is installed at a Windows path, which is how a Windows host is told from any other
    /// before DssHarness runs there.
    /// </summary>
    public bool OnWindows => Platform.PlatformPaths.NamesADrive(Location) || Location.StartsWith(@"\\", StringComparison.Ordinal);
}

/// <summary>
/// Reads what programs on a host print. Each reader accepts only the shape it knows and reports
/// anything else as unreadable, because a guess at a host's state is exactly how a leg ends up
/// running somewhere it cannot.
/// </summary>
public static partial class HostProbes
{
    /// <summary>ssh's own exit code for a failure of ssh itself, such as a connection or authentication failure.</summary>
    public const int SshFailed = 255;

    /// <summary>Longest excerpt of a program's output a message quotes.</summary>
    private const int ExcerptLength = 300;

    /// <summary>
    /// Whether the host ran a command at all. A program that ran and failed exits non-zero; any
    /// failure of ssh's own - a connection that never opened, or one that closed - is its
    /// <see cref="SshFailed"/>; wsl.exe that failed itself
    /// exits with a code no program in a distribution can, a negative one, as Windows reports
    /// 0xFFFFFFFF, where a program's own status is 0 to 255; and one that hung has no exit code to
    /// read. Only an answer says anything about the host.
    /// </summary>
    /// <param name="result">What running the command produced.</param>
    public static bool Answered(Processes.ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !result.TimedOut && result.ExitCode != SshFailed && result.ExitCode >= 0;
    }

    /// <summary>Reads <c>uname -sm</c> into an operating system and a processor, in configuration's words.</summary>
    public static (string? Os, string? Processor) ReadUname(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var words = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return words.Length < 2
            ? (null, null)
            : (PlatformNames.ForKernel(words[0]), PlatformNames.ForMachine(words[^1]));
    }

    /// <summary>Reads <c>dotnet --list-sdks</c>, skipping any line that is not an SDK, such as a first-run banner.</summary>
    public static IReadOnlyList<SdkListing> ReadSdks(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return [.. output
            .Split('\n')
            .Select(line => SdkLine().Match(line.Trim()))
            .Where(match => match.Success)
            .Select(match => new SdkListing(match.Groups["version"].Value, match.Groups["location"].Value))];
    }

    /// <summary>
    /// Reads the installed version of <paramref name="packageId"/> from
    /// <c>dotnet tool list --global --format json</c>.
    /// </summary>
    /// <returns>
    /// Whether the text is that document. <paramref name="version"/> is <see langword="null"/> when it is
    /// and the tool is not installed.
    /// </returns>
    public static bool TryReadToolVersion(string output, string packageId, out string? version)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        version = null;

        // The document is found rather than assumed to start the output: a first-run banner can come first.
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output.AsMemory(start));

            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var tool in data.EnumerateArray())
            {
                // NuGet package IDs compare ignoring case, and the listing writes them in lower case.
                if (tool.TryGetProperty("packageId", out var id)
                    && string.Equals(id.GetString(), packageId, StringComparison.OrdinalIgnoreCase))
                {
                    version = tool.TryGetProperty("version", out var installed) ? installed.GetString() : null;
                    return version is not null;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether wsl.exe said that no distribution has the name it was given.</summary>
    /// <remarks>
    /// Matched on the error code wsl.exe prints, never on its sentence, which is translated into the
    /// machine's language.
    /// </remarks>
    public static bool IsMissingDistribution(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.Ordinal);
    }

    /// <summary>Whether WSL could not start <paramref name="program"/> in a distribution because the distribution has no such program.</summary>
    public static bool IsMissingProgramInWsl(string text, string program)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains($"execvpe({program}) failed", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a process listing shows <paramref name="program"/> running: the output of
    /// <c>ps -A -o comm=</c>, one name or path per line, or of <c>tasklist /FO CSV /NH</c>, which starts
    /// each line with the quoted image name.
    /// </summary>
    public static bool ListsProcess(string listing, string program)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var image = line[0] == '"' ? line[1..].Split('"')[0] : line;
            var name = Path.GetFileName(image.Replace('\\', '/'));

            if (string.Equals(name, program, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, program + ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How a program that did not do what was asked is reported: what failed, then how it failed, in
    /// the program's own words. One helper so that every reason reads the same and none of them
    /// silently drops what the program said, which is usually the only thing that identifies the fault.
    /// Where ssh never connected to the host to start it, that is said instead, as <see cref="Unreached"/>
    /// says it: nothing the program would have done is any part of the reason.
    /// </summary>
    /// <param name="what">What was being done, as a lower-case fragment, with any remedy after a semicolon.</param>
    /// <param name="result">What the program did.</param>
    /// <param name="through">The connection it was started through, where one carried it to a host.</param>
    public static string Failure(string what, ProcessResult result, HostConnection? through = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(result);

        if (Unreached(result, through) is { } unreached)
        {
            return unreached;
        }

        if (result.TimedOut)
        {
            return $"{what}: there was no answer within {result.Duration.TotalSeconds:0} seconds";
        }

        var said = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return $"{what} (exit {result.ExitCode}): {Excerpt(said)}";
    }

    /// <summary>
    /// That <paramref name="through"/>'s host could not be reached, in ssh's own words, where ssh never
    /// connected to it to start <paramref name="result"/>'s program - see <see cref="NeverConnected"/> - or
    /// <see langword="null"/>: for a program that ran, one this machine or WSL started, and an ssh failure
    /// after the host answered - a key refused, or a connection that ended part way: the host was reached.
    /// </summary>
    /// <param name="result">What starting a program through the connection produced.</param>
    /// <param name="through">The connection, or <see langword="null"/> for a program this machine ran itself.</param>
    /// <remarks>
    /// Said before whatever the program was asked to do, because nothing of it happened: reported as
    /// DssHarness not answering from its tool path, a name that did not resolve sent the reader after an
    /// install that was fine, with the reason ssh gave further along the same line.
    /// </remarks>
    public static string? Unreached(ProcessResult result, HostConnection? through)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !result.TimedOut
            && through?.Host.Kind == HostKind.Ssh
            && result.ExitCode == SshFailed
            && NeverConnected(result.StandardError) is { } said
                ? CouldNotReach(said)
                : null;
    }

    /// <summary>That the host could not be reached, with what ssh said about it.</summary>
    /// <param name="said">ssh's words, quoted as they are.</param>
    public static string CouldNotReach(string said) => $"the host could not be reached: ssh said {said}";

    /// <summary>
    /// The line in which ssh said it never connected to the host - the name did not resolve, or nothing
    /// took the connection at the address - or <see langword="null"/> where it said no such thing.
    /// </summary>
    /// <param name="standardError">What ssh printed on standard error.</param>
    /// <remarks>
    /// Measured with OpenSSH for Windows 9.5p2 and 10.0p2 and Git for Windows' 10.5p1. A name that does not
    /// resolve is "ssh: Could not resolve hostname", and an address where nothing answers "ssh: connect to
    /// host ... port ...: Connection timed out", from each. A refused connection is "ssh: connect to host"
    /// from Git's, and "banner exchange: Connection to UNKNOWN port -1" from the Windows builds, whose
    /// connection has no far end to name. Anything else ssh fails over came after the host answered.
    /// </remarks>
    public static string? NeverConnected(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        return standardError
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => SshNeverConnected().IsMatch(line));
    }

    /// <summary>
    /// Whether ssh failed, before any session began, for a reason the address it was given can be to blame
    /// for - it never connected, or it refused the key the host showed - so that nothing ran there, and a
    /// call made again runs nothing twice. A login refused is not one: the host whose key was accepted refused
    /// it, and would refuse it again.
    /// </summary>
    /// <param name="result">What an ssh call produced.</param>
    /// <remarks>
    /// Measured with the same three clients: a key known_hosts does not hold, or holds another of, ends with
    /// "Host key verification failed." from each.
    /// </remarks>
    public static bool FailedBeforeAnySession(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return !result.TimedOut
            && result.ExitCode == SshFailed
            && (NeverConnected(result.StandardError) is not null
                || result.StandardError.Contains("Host key verification failed.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Why a program a host was asked to run never reported how it finished: that the host could not be
    /// reached, as <see cref="Unreached"/> says it, where ssh never connected; otherwise that the program
    /// may not have run, or run only in part, with the exit the connection ended with and what it said last.
    /// </summary>
    /// <param name="what">The program as the reader knows it: its name quoted, with anything that tells it apart.</param>
    /// <param name="result">What running it through the connection produced.</param>
    /// <param name="through">The connection it was run through.</param>
    public static string NeverFinished(string what, ProcessResult result, HostConnection through)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(result);

        return Unreached(result, through)
            ?? $"{what} never reported how it finished, so it may not have run, or run only in part; "
                + $"the connection ended with exit {result.ExitCode}{Detail(result.StandardError)}";
    }

    /// <summary>
    /// What a program printed on standard error, quoted after a colon as <see cref="Excerpt"/> quotes it,
    /// or nothing where it printed nothing.
    /// </summary>
    /// <param name="standardError">What it printed.</param>
    public static string Detail(string standardError)
    {
        var said = Excerpt(standardError);

        return said.Length == 0 ? string.Empty : ": " + said;
    }

    /// <summary>
    /// A short quotation of what a program printed, on one line, keeping the end, where a program
    /// usually says what went wrong.
    /// </summary>
    public static string Excerpt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var joined = string.Join(" / ", text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));

        return joined.Length <= ExcerptLength ? joined : "..." + joined[^ExcerptLength..];
    }

    [GeneratedRegex(@"^(?:ssh(?:\.exe)?: (?:Could not resolve hostname |connect to host \S+ port \S+: )|banner exchange: Connection to UNKNOWN port -1: )", RegexOptions.CultureInvariant)]
    private static partial Regex SshNeverConnected();

    [GeneratedRegex(@"^(?<version>\d+\.\d+\.\d+\S*)\s+\[(?<location>.+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkLine();
}
