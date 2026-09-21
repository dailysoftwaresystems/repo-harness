using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>What looking for a developer environment on a host found.</summary>
/// <remarks>
/// Three facts, kept apart as a program's are: one looked for and found, one looked for and not
/// there, and one the host could not look for. A look that failed must not read as one that looked
/// and found nothing: that sent a reader to reinstall Visual Studio over an installer that timed out.
/// </remarks>
public enum DeveloperEnvironmentFound
{
    /// <summary>An instance with what it requires is there: the one that would be set up.</summary>
    Installed,

    /// <summary>The host looked, and has none: what a leg starting in it needs is not installed there.</summary>
    Nowhere,

    /// <summary>
    /// The host could not look - its installer's vswhere would not start, did not answer, or answered
    /// in a form this build cannot read - so nothing is known either way, and no refusal may call it
    /// missing.
    /// </summary>
    Unreadable,
}

/// <summary>Whether a developer environment can be set up on a host, and which instance would be.</summary>
/// <param name="Found">What looking for it found.</param>
/// <param name="Reason">Why it cannot be set up there, naming what was looked for, when it cannot.</param>
/// <param name="InstallationPath">The instance that would be set up, when there is one.</param>
/// <param name="InstallationVersion">That instance's version, as its installer reports it.</param>
public sealed record DeveloperEnvironmentCheck(DeveloperEnvironmentFound Found, string? Reason, string? InstallationPath, string? InstallationVersion)
{
    /// <summary>The instance that would be set up there.</summary>
    /// <param name="installationPath">Where it is.</param>
    /// <param name="installationVersion">Its version, as its installer reports it.</param>
    public static DeveloperEnvironmentCheck Installed(string installationPath, string? installationVersion)
        => new(DeveloperEnvironmentFound.Installed, null, installationPath, installationVersion);

    /// <summary>None there, and why, naming what was looked for.</summary>
    /// <param name="reason">Why, naming what was looked for.</param>
    public static DeveloperEnvironmentCheck Nowhere(string reason) => new(DeveloperEnvironmentFound.Nowhere, reason, null, null);

    /// <summary>Whether one is there could not be established, and why.</summary>
    /// <param name="reason">Why the host could not look.</param>
    public static DeveloperEnvironmentCheck Unreadable(string reason) => new(DeveloperEnvironmentFound.Unreadable, reason, null, null);

    /// <summary>
    /// Whether it can be set up there: installed, and the instance named. What placing a leg and
    /// setting it up both ask, so a check one would take the other cannot refuse.
    /// </summary>
    [JsonIgnore]
    public bool CanSetUp => Found == DeveloperEnvironmentFound.Installed && InstallationPath is { Length: > 0 };
}

/// <summary>
/// Finds, on the machine it runs on, the instance a developer environment would be set up from:
/// for Visual Studio, the newest instance holding the component it requires, as the installer's own
/// <c>vswhere</c> reports it.
/// </summary>
/// <remarks>
/// A look, and nothing set up: the survey asks it of every host a leg naming one might land on, and
/// a host without it turns that leg away as missing a tool, naming what was looked for - never a
/// build that fails halfway for want of <c>cl</c>.
/// </remarks>
public sealed class DeveloperEnvironmentProbe(IHostPlatform platform, IProcessRunner processRunner)
{
    /// <summary>Longest vswhere may take.</summary>
    public static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(60);

    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;

    /// <summary>
    /// Where Visual Studio's installer keeps vswhere: the one place it is always installed, since
    /// Visual Studio 2017, whatever else is on the PATH.
    /// </summary>
    public static string VsWherePath => Path.Combine(
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programs ? programs : @"C:\Program Files (x86)",
        "Microsoft Visual Studio",
        "Installer",
        "vswhere.exe");

    /// <summary>Checks <paramref name="environment"/> on this machine.</summary>
    /// <param name="environment">The developer environment.</param>
    /// <param name="cancellationToken">Stops the look.</param>
    public async Task<DeveloperEnvironmentCheck> CheckAsync(DeveloperEnvironmentConfig environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!string.Equals(environment.Kind, DeveloperEnvironmentKinds.VisualStudio, StringComparison.OrdinalIgnoreCase))
        {
            return DeveloperEnvironmentCheck.Nowhere($"this build does not know how to set up a '{environment.Kind}' environment");
        }

        if (!string.Equals(_platform.PlatformKey, PlatformNames.Windows, StringComparison.Ordinal))
        {
            return DeveloperEnvironmentCheck.Nowhere($"Visual Studio is set up on windows, and this host runs {_platform.PlatformKey}");
        }

        var vswhere = VsWherePath;
        ProcessResult result;

        try
        {
            // JSON rather than one property, so the instance and its version come from one answer;
            // UTF-8, so a path the console's code page cannot spell arrives whole.
            result = await _processRunner
                .RunAsync(
                    new ProcessRequest
                    {
                        FileName = vswhere,
                        Arguments = ["-latest", "-products", "*", "-requires", environment.RequiresComponent, "-format", "json", "-utf8"],
                        Timeout = ProbeBudget,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExecutableNotFoundException)
        {
            return DeveloperEnvironmentCheck.Nowhere($"Visual Studio's installer is not there: '{vswhere}' was not found");
        }
        catch (ProgramStartException ex)
        {
            return DeveloperEnvironmentCheck.Unreadable($"'{vswhere}' could not be started: {ex.Message}");
        }

        if (result.TimedOut || result.ExitCode != 0)
        {
            return DeveloperEnvironmentCheck.Unreadable(HostProbes.Failure($"'{vswhere}' could not list the Visual Studio instances", result));
        }

        return Read(result.StandardOutput, environment.RequiresComponent);
    }

    /// <summary>
    /// The instance vswhere's JSON answer names, or why it names none.
    /// </summary>
    /// <param name="json">What vswhere printed.</param>
    /// <param name="component">The component it was asked for, for a refusal to name.</param>
    internal static DeveloperEnvironmentCheck Read(string json, string component)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } instance
                && instance.TryGetProperty("installationPath", out var path)
                && path.ValueKind == JsonValueKind.String
                && path.GetString() is { Length: > 0 } installationPath)
            {
                var version = instance.TryGetProperty("installationVersion", out var declared) && declared.ValueKind == JsonValueKind.String
                    ? declared.GetString()
                    : null;

                return DeveloperEnvironmentCheck.Installed(installationPath, version);
            }

            return DeveloperEnvironmentCheck.Nowhere($"no Visual Studio instance there has the component '{component}'");
        }
        catch (JsonException ex)
        {
            return DeveloperEnvironmentCheck.Unreadable($"vswhere answered in a form this build cannot read: {ex.Message}");
        }
    }
}
