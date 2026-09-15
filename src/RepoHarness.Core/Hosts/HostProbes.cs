using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoHarness.Core.Platform;

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
    public bool OnWindows => (Location.Length >= 2 && Location[1] == ':') || Location.StartsWith(@"\\", StringComparison.Ordinal);
}

/// <summary>
/// Reads what programs on a host print. Each reader accepts only the shape it knows and reports
/// anything else as unreadable, because a guess at a host's state is exactly how a leg ends up
/// running somewhere it cannot.
/// </summary>
public static partial class HostProbes
{
    /// <summary>Longest excerpt of a program's output a message quotes.</summary>
    private const int ExcerptLength = 300;

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

    [GeneratedRegex(@"^(?<version>\d+\.\d+\.\d+\S*)\s+\[(?<location>.+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkLine();
}
