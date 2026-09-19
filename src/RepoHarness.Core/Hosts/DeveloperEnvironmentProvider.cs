using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>Which developer environment a leg's run was set up with.</summary>
/// <param name="Name">The environment, as <c>developerEnvironments</c> names it.</param>
/// <param name="InstallationPath">The Visual Studio instance it came from.</param>
/// <param name="InstallationVersion">That instance's version, as its installer reports it.</param>
/// <param name="ToolsVersion">The C++ tools version it set up, as <c>VCToolsVersion</c> says.</param>
/// <param name="Architecture">What vcvarsall.bat was asked for: the host's processor, then the leg's.</param>
public sealed record DeveloperEnvironmentFact(
    string Name,
    string InstallationPath,
    string InstallationVersion,
    string ToolsVersion,
    string Architecture)
{
    /// <summary>The fact as a leg's line names it.</summary>
    public string Describe()
        => $"developer environment: {Name} (Visual Studio {InstallationVersion}, MSVC {ToolsVersion}, {Architecture})";
}

/// <summary>A developer environment set up for one leg, or why it could not be.</summary>
/// <param name="Environment">What it adds to, or changes in, the environment every process of the leg starts with.</param>
/// <param name="Fact">Which instance and tools it came from.</param>
/// <param name="Unavailable">Why it could not be set up, naming what was looked for; <see langword="null"/> when it was.</param>
public sealed record DeveloperEnvironmentSetup(
    IReadOnlyDictionary<string, string> Environment,
    DeveloperEnvironmentFact? Fact,
    string? Unavailable)
{
    /// <summary>One that could not be set up, and why.</summary>
    public static DeveloperEnvironmentSetup Refused(string why)
        => new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, why);
}

/// <summary>
/// Sets up a developer environment on the machine that runs the leg: runs the <c>vcvarsall.bat</c> of
/// the Visual Studio instance the survey of this machine found, for the leg's processor, and keeps
/// what that changed in the environment - once per environment, instance, architecture and host
/// environment, however many legs share them.
/// </summary>
/// <remarks>
/// <para>
/// What <c>vcvarsall.bat</c> sets is read from <c>cmd.exe</c> itself, running a batch file the
/// harness writes for the purpose: <c>set</c> before, <c>call vcvarsall.bat</c>, its exit code, and
/// <c>set</c> after, each to a file of its own in UTF-16 (<c>/u</c>), so a path the console's code
/// page cannot spell arrives whole. The instance's path and the architecture reach the batch file
/// in its environment rather than in its text, so neither is ever parsed as batch syntax. This is
/// the one place the harness hands a command to a shell: a batch file has no other interpreter.
/// </para>
/// <para>
/// vcvarsall.bat can report a failure and exit 0, so its own output is read for its
/// <c>[ERROR</c> lines as well as its exit code, and <c>VSCMD_ARG_TGT_ARCH</c> must name the
/// processor asked for: an environment set up for another processor builds code for that one.
/// </para>
/// </remarks>
public sealed class DeveloperEnvironmentProvider(
    IHostPlatform platform,
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    IHarnessOutput output)
{
    /// <summary>What this reports under.</summary>
    public const string CommandName = "developer-environment";

    /// <summary>Longest vcvarsall.bat may take.</summary>
    public static readonly TimeSpan CaptureBudget = TimeSpan.FromMinutes(2);

    /// <summary>What the batch file is given the instance's vcvarsall.bat in.</summary>
    internal const string ScriptVariable = "DSSHARNESS_VCVARSALL";

    /// <summary>What the batch file is given the architecture in.</summary>
    internal const string ArchitectureVariable = "DSSHARNESS_VCVARSALL_ARCH";

    /// <summary>
    /// The batch file: each <c>set</c> to its own file beside it, and the exit code redirected first,
    /// so a code of 1 is never read as a redirect of handle 1.
    /// </summary>
    internal const string CaptureScript =
        "@echo off\r\n"
        + "set > \"%~dp0before.env\"\r\n"
        + "call \"%" + ScriptVariable + "%\" %" + ArchitectureVariable + "% > \"%~dp0vcvarsall.log\" 2>&1\r\n"
        + "> \"%~dp0exit.txt\" echo %ERRORLEVEL%\r\n"
        + "set > \"%~dp0after.env\"\r\n";

    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly ConcurrentDictionary<(string Name, string Instance, string Architecture, string Over), Lazy<Task<DeveloperEnvironmentSetup>>> _captured = new();

    /// <summary>
    /// Sets up the developer environment <paramref name="name"/> from the instance
    /// <paramref name="found"/> names, for a leg built for <paramref name="processor"/>, over
    /// <paramref name="hostEnvironment"/>.
    /// </summary>
    /// <param name="name">The environment, as <c>developerEnvironments</c> names it.</param>
    /// <param name="found">What the survey of this machine found for it: the instance that is set up.</param>
    /// <param name="processor">The processor the leg is built for.</param>
    /// <param name="hostEnvironment">What the host declares under <c>env</c>, which it is set up over.</param>
    /// <param name="cancellationToken">Stops the setup.</param>
    /// <remarks>
    /// From the survey's answer rather than a look of its own, so the instance set up is the one the
    /// survey reported and placed the leg by. One removed since is refused here, its vcvarsall.bat gone.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="found"/> names no instance: a leg is only ever placed where one was found.
    /// </exception>
    public Task<DeveloperEnvironmentSetup> SetUpAsync(
        string name,
        DeveloperEnvironmentCheck found,
        string processor,
        IReadOnlyDictionary<string, string> hostEnvironment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentException.ThrowIfNullOrWhiteSpace(processor);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        if (found is not { Available: true, InstallationPath: { Length: > 0 } instance })
        {
            throw new ArgumentException($"Developer environment '{name}' was set up where the survey found no instance of it.", nameof(found));
        }

        return SetUpCoreAsync(name, found, instance, processor, hostEnvironment, cancellationToken);
    }

    private async Task<DeveloperEnvironmentSetup> SetUpCoreAsync(
        string name,
        DeveloperEnvironmentCheck found,
        string instance,
        string processor,
        IReadOnlyDictionary<string, string> hostEnvironment,
        CancellationToken cancellationToken)
    {
        if (VisualStudioArchitecture.For(_platform.Processor, processor) is not { } architecture)
        {
            return DeveloperEnvironmentSetup.Refused(
                $"developer environment '{name}' has no vcvarsall.bat architecture for a {processor} leg on a {_platform.Processor} host");
        }

        // Once per environment, instance, architecture and the environment it is set up over: legs
        // sharing them share what vcvarsall.bat set, and running it again for each would only take
        // longer to say the same thing. The host's environment is part of the key because what
        // vcvarsall.bat sets is built on it - a PATH it puts Visual Studio's directories ahead of is the
        // PATH it was given - and the name is, because what was set up is reported, and refused, under it.
        var key = (name, instance, architecture, Over(hostEnvironment));

        while (true)
        {
            var captured = _captured.GetOrAdd(
                key,
                _ => new Lazy<Task<DeveloperEnvironmentSetup>>(
                    () => CaptureAsync(name, instance, found.InstallationVersion, architecture, hostEnvironment, cancellationToken)));

            try
            {
                return await captured.Value.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Stopped by the caller that started it, not by this one, which still wants it: that
                // capture is forgotten - only if it is still the one kept - and this caller runs its own.
                _captured.TryRemove(new KeyValuePair<(string, string, string, string), Lazy<Task<DeveloperEnvironmentSetup>>>(key, captured));
            }
        }
    }

    /// <summary>
    /// <paramref name="hostEnvironment"/> as one string, the same for the same variables in any order
    /// or spelling of their names.
    /// </summary>
    private static string Over(IReadOnlyDictionary<string, string> hostEnvironment)
        => string.Join(
            "\0",
            hostEnvironment
                .Select(pair => (Name: pair.Key.ToUpperInvariant(), pair.Value))
                .OrderBy(pair => pair.Name, StringComparer.Ordinal)
                .Select(pair => pair.Name + "=" + pair.Value));

    /// <summary>Runs <paramref name="instance"/>'s vcvarsall.bat once and keeps what it changed.</summary>
    private async Task<DeveloperEnvironmentSetup> CaptureAsync(
        string name,
        string instance,
        string? version,
        string architecture,
        IReadOnlyDictionary<string, string> hostEnvironment,
        CancellationToken cancellationToken)
    {
        var vcvarsall = Path.Combine(instance, "VC", "Auxiliary", "Build", "vcvarsall.bat");

        if (!_fileSystem.FileExists(vcvarsall))
        {
            return DeveloperEnvironmentSetup.Refused(
                $"developer environment '{name}' found Visual Studio at '{instance}', which has no '{vcvarsall}'");
        }

        var scratch = Path.Combine(Path.GetTempPath(), "dssharness-vcvars-" + Guid.NewGuid().ToString("N"));

        try
        {
            _fileSystem.CreateDirectory(scratch);
            _fileSystem.WriteAllTextAtomic(Path.Combine(scratch, "capture.bat"), CaptureScript);

            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (key, value) in hostEnvironment)
            {
                environment[key] = value;
            }

            environment[ScriptVariable] = vcvarsall;
            environment[ArchitectureVariable] = architecture;

            var shell = System.Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";
            ProcessResult result;

            try
            {
                // Started by a relative path from its own directory, so the scratch directory's name -
                // a user's profile can hold spaces and brackets - never reaches cmd.exe's command line.
                result = await _processRunner
                    .RunAsync(
                        new ProcessRequest
                        {
                            FileName = shell,
                            Arguments = ["/d", "/u", "/c", @".\capture.bat"],
                            WorkingDirectory = scratch,
                            Environment = environment,
                            Timeout = CaptureBudget,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProgramStartException ex)
            {
                return DeveloperEnvironmentSetup.Refused(
                    $"developer environment '{name}': vcvarsall.bat {architecture} could not be run, because '{shell}' could not be started: {ex.Message}");
            }

            return Read(name, instance, version, architecture, scratch, result);
        }
        finally
        {
            RemoveScratch(name, scratch);
        }
    }

    /// <summary>
    /// Removes the directory the capture ran in. One that cannot be removed - a scanner still holding
    /// a file written there - is said, and fails nothing: what vcvarsall.bat set was read before, and
    /// a directory left behind in the temporary directory costs less than the leg it would fail.
    /// </summary>
    private void RemoveScratch(string name, string scratch)
    {
        try
        {
            _fileSystem.DeleteDirectory(scratch);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _output.Warn(CommandName, $"developer environment '{name}': '{scratch}' could not be removed: {exception.Message}");
        }
    }

    /// <summary>What one run of the batch file left beside it, read into a setup or a refusal.</summary>
    private DeveloperEnvironmentSetup Read(
        string name,
        string instance,
        string? version,
        string architecture,
        string scratch,
        ProcessResult result)
    {
        string Written(string file) => Path.Combine(scratch, file);

        if (result.TimedOut)
        {
            return DeveloperEnvironmentSetup.Refused(
                $"developer environment '{name}': vcvarsall.bat {architecture} did not finish within {CaptureBudget.TotalSeconds:0} seconds");
        }

        if (!_fileSystem.FileExists(Written("after.env")) || !_fileSystem.FileExists(Written("exit.txt")))
        {
            return DeveloperEnvironmentSetup.Refused(
                $"developer environment '{name}': vcvarsall.bat {architecture} could not be run: {HostProbes.Failure("cmd.exe", result)}");
        }

        var log = _fileSystem.FileExists(Written("vcvarsall.log")) ? ReadBytes(Written("vcvarsall.log")) : [];
        var exit = Unicode(ReadBytes(Written("exit.txt"))).Trim();

        if (!int.TryParse(exit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) || code != 0)
        {
            return DeveloperEnvironmentSetup.Refused(
                $"developer environment '{name}': vcvarsall.bat {architecture} exited {exit}: {Said(log)}");
        }

        if (ReportsAnError(log))
        {
            return DeveloperEnvironmentSetup.Refused(
                $"developer environment '{name}': vcvarsall.bat {architecture} reported an error: {Said(log)}");
        }

        var before = Variables(Unicode(ReadBytes(Written("before.env"))));
        var after = Variables(Unicode(ReadBytes(Written("after.env"))));
        var target = VisualStudioArchitecture.Target(architecture);

        if (!after.TryGetValue("VSCMD_ARG_TGT_ARCH", out var reported) || !string.Equals(reported, target, StringComparison.OrdinalIgnoreCase))
        {
            return DeveloperEnvironmentSetup.Refused(
                $"developer environment '{name}': vcvarsall.bat {architecture} set up VSCMD_ARG_TGT_ARCH '{reported ?? string.Empty}', "
                + $"where '{target}' was asked for");
        }

        // What it added or changed, and nothing else: the process's own environment is inherited by
        // every child anyway, and carrying it twice would make this machine's whole environment part
        // of what a leg reports it was set up with.
        var changed = after
            .Where(pair => !before.TryGetValue(pair.Key, out var was) || !string.Equals(was, pair.Value, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return new DeveloperEnvironmentSetup(
            changed,
            new DeveloperEnvironmentFact(
                name,
                instance,
                version ?? after.GetValueOrDefault("VSCMD_VER") ?? "unknown",
                after.GetValueOrDefault("VCToolsVersion") ?? "unknown",
                architecture),
            null);
    }

    private byte[] ReadBytes(string path)
    {
        using var stream = _fileSystem.OpenRead(path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }

    /// <summary>What cmd.exe wrote under <c>/u</c>: UTF-16, little-endian.</summary>
    private static string Unicode(byte[] bytes) => Encoding.Unicode.GetString(bytes);

    /// <summary>The variables <c>set</c> listed, one <c>NAME=value</c> to a line.</summary>
    internal static Dictionary<string, string> Variables(string listing)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in listing.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            var separator = text.IndexOf('=', StringComparison.Ordinal);

            // A name cannot be empty; a line with none, or starting with '=', is not a variable.
            if (separator > 0)
            {
                variables[text[..separator]] = text[(separator + 1)..];
            }
        }

        return variables;
    }

    /// <summary>
    /// Whether vcvarsall.bat printed one of its <c>[ERROR</c> lines, which it does and still exits 0.
    /// </summary>
    /// <remarks>
    /// Its output is two encodings in one file: its own lines are cmd.exe's, in UTF-16 under
    /// <c>/u</c>, and what the programs it starts print is in the console's code page. So the bytes
    /// are searched for both spellings rather than decoded one way.
    /// </remarks>
    internal static bool ReportsAnError(byte[] log)
        => Contains(log, Encoding.ASCII.GetBytes("[ERROR")) || Contains(log, Encoding.Unicode.GetBytes("[ERROR"));

    /// <summary>What vcvarsall.bat said, for a refusal: the text of either encoding, its NULs dropped.</summary>
    internal static string Said(byte[] log)
        => HostProbes.Excerpt(Encoding.Latin1.GetString(log).Replace("\0", string.Empty, StringComparison.Ordinal));

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}

/// <summary>What vcvarsall.bat is asked for, and what it reports having set up.</summary>
internal static class VisualStudioArchitecture
{
    private static readonly Dictionary<string, string> Tokens = new(StringComparer.OrdinalIgnoreCase)
    {
        [PlatformNames.X64] = "amd64",
        [PlatformNames.Arm64] = "arm64",
        ["x86"] = "x86",
        ["arm"] = "arm",
    };

    /// <summary>
    /// The argument vcvarsall.bat takes for building on a <paramref name="host"/> machine for a
    /// <paramref name="target"/> leg - <c>amd64</c>, or <c>amd64_arm64</c> to cross-compile - or
    /// <see langword="null"/> for a processor it has no word for.
    /// </summary>
    public static string? For(string host, string target)
        => Tokens.TryGetValue(host, out var from) && Tokens.TryGetValue(target, out var to)
            ? (from == to ? to : $"{from}_{to}")
            : null;

    /// <summary>What <c>VSCMD_ARG_TGT_ARCH</c> says after vcvarsall.bat is given <paramref name="architecture"/>.</summary>
    public static string Target(string architecture)
        => (architecture.Contains('_', StringComparison.Ordinal) ? architecture[(architecture.IndexOf('_', StringComparison.Ordinal) + 1)..] : architecture) switch
        {
            "amd64" => "x64",
            var other => other,
        };
}
