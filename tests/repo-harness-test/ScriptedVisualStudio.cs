using System.Text;
using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// Visual Studio as its installer's vswhere and cmd.exe running vcvarsall.bat show it, with neither
/// installed: an instance in a directory of its own, holding a vcvarsall.bat, and a capture that
/// writes beside the batch file what cmd.exe's own <c>set</c> would, in the UTF-16 it writes under
/// <c>/u</c>.
/// </summary>
/// <remarks>
/// What the capture starts from is what cmd.exe would have: a PATH and a PROMPT of its own, and every
/// variable the request sets over them. vcvarsall.bat then puts its tools ahead on PATH and sets
/// INCLUDE, LIB, the tools version and the processor it set up for.
/// </remarks>
internal sealed class ScriptedVisualStudio : IProcessRunner, IDisposable
{
    /// <summary>The instance's version, as its installer reports it.</summary>
    public const string InstallationVersion = "18.0.11205.157";

    /// <summary>The C++ tools version vcvarsall.bat sets up.</summary>
    public const string ToolsVersion = "14.50.35717";

    /// <summary>The PATH cmd.exe starts with where the request sets none.</summary>
    public const string ShellPath = @"C:\Windows\system32";

    private readonly TempDirectory _instance = new();
    private readonly List<ProcessRequest> _captures = [];
    private readonly List<ProcessRequest> _probes = [];

    public ScriptedVisualStudio()
    {
        _instance.WriteFile(Path.Combine("VC", "Auxiliary", "Build", "vcvarsall.bat"), "@echo off\r\n");
    }

    /// <summary>Where the instance is.</summary>
    public string InstallationPath => _instance.Path;

    /// <summary>The instance as a survey of the machine reports it.</summary>
    public DeveloperEnvironmentCheck Found => new(true, null, InstallationPath, InstallationVersion);

    /// <summary>What vswhere prints; the instance, when left out.</summary>
    public string? Instances { get; init; }

    /// <summary>What vswhere exits with.</summary>
    public int VsWhereExitCode { get; init; }

    /// <summary>What the batch file writes to <c>exit.txt</c>: vcvarsall.bat's exit code.</summary>
    public string ExitCode { get; init; } = "0";

    /// <summary>What vcvarsall.bat printed, as the bytes its log holds.</summary>
    public byte[] Log { get; init; } = [];

    /// <summary>What <c>VSCMD_ARG_TGT_ARCH</c> says after; the processor asked for, when left out.</summary>
    public string? ReportedTarget { get; init; }

    /// <summary>Whether cmd.exe never ran the batch file, and so wrote nothing beside it.</summary>
    public bool WritesNothing { get; init; }

    /// <summary>Whether the batch file stopped before its last <c>set</c>, leaving no listing of what came after.</summary>
    public bool WritesNoListingAfter { get; init; }

    /// <summary>Whether the capture outlives its budget.</summary>
    public bool TimesOut { get; init; }

    /// <summary>Whether cmd.exe is nowhere to be started.</summary>
    public bool ShellMissing { get; init; }

    /// <summary>
    /// What each capture waits on before it writes anything, given which capture it is - the first is
    /// 0 - and what stops it; nothing, when left out.
    /// </summary>
    public Func<int, CancellationToken, Task>? Holding { get; init; }

    /// <summary>Every capture started, in order.</summary>
    public IReadOnlyList<ProcessRequest> Captures
    {
        get
        {
            lock (_captures)
            {
                return [.. _captures];
            }
        }
    }

    /// <summary>Every time vswhere was asked, in order.</summary>
    public IReadOnlyList<ProcessRequest> Probes
    {
        get
        {
            lock (_probes)
            {
                return [.. _probes];
            }
        }
    }

    /// <summary>The directory vcvarsall.bat puts first on PATH for a build for <paramref name="target"/>.</summary>
    /// <param name="target">What <c>VSCMD_ARG_TGT_ARCH</c> says: <c>x64</c>, <c>arm64</c>.</param>
    public string BinFor(string target) => Path.Combine(InstallationPath, "VC", "Tools", "MSVC", ToolsVersion, "bin", "Hostx64", target);

    /// <summary>The INCLUDE vcvarsall.bat sets.</summary>
    public string Include => Path.Combine(InstallationPath, "VC", "Tools", "MSVC", ToolsVersion, "include");

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(request.FileName, DeveloperEnvironmentProbe.VsWherePath, StringComparison.OrdinalIgnoreCase))
        {
            lock (_probes)
            {
                _probes.Add(request);
            }

            var answer = Instances ?? JsonSerializer.Serialize(new[] { new { installationPath = InstallationPath, installationVersion = InstallationVersion } });

            return new ProcessResult(VsWhereExitCode, answer, VsWhereExitCode == 0 ? string.Empty : "vswhere: no instances", TimeSpan.Zero, TimedOut: false);
        }

        int index;

        lock (_captures)
        {
            index = _captures.Count;
            _captures.Add(request);
        }

        if (Holding is { } holding)
        {
            await holding(index, cancellationToken);
        }

        if (ShellMissing)
        {
            throw new ExecutableNotFoundException(request.FileName);
        }

        if (TimesOut)
        {
            return new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: true);
        }

        if (WritesNothing)
        {
            return new ProcessResult(1, string.Empty, "The system cannot find the path specified.", TimeSpan.Zero, TimedOut: false);
        }

        var directory = request.WorkingDirectory!;
        var architecture = request.Environment[DeveloperEnvironmentProvider.ArchitectureVariable]!;
        var target = VisualStudioArchitecture.Target(architecture);

        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = ShellPath, ["PROMPT"] = "$P$G" };

        foreach (var (name, value) in request.Environment)
        {
            if (value is not null)
            {
                before[name] = value;
            }
        }

        var after = new Dictionary<string, string>(before, StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = BinFor(target) + ";" + before["PATH"],
            ["INCLUDE"] = Include,
            ["LIB"] = Path.Combine(InstallationPath, "VC", "Tools", "MSVC", ToolsVersion, "lib", target),
            ["VCToolsVersion"] = ToolsVersion,
            ["VSCMD_ARG_TGT_ARCH"] = ReportedTarget ?? target,
            ["VSCMD_VER"] = "18.0.0",
        };

        File.WriteAllBytes(Path.Combine(directory, "before.env"), Encoding.Unicode.GetBytes(Listing(before)));

        if (!WritesNoListingAfter)
        {
            File.WriteAllBytes(Path.Combine(directory, "after.env"), Encoding.Unicode.GetBytes(Listing(after)));
        }

        File.WriteAllBytes(Path.Combine(directory, "exit.txt"), Encoding.Unicode.GetBytes(ExitCode + "\r\n"));
        File.WriteAllBytes(Path.Combine(directory, "vcvarsall.log"), Log);

        return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false);
    }

    public string? FindExecutable(string command) => command;

    public void Dispose() => _instance.Dispose();

    /// <summary>A provider that sets up this instance, on a Windows machine with <paramref name="processor"/>.</summary>
    /// <param name="processor">The machine's processor.</param>
    /// <param name="fileSystem">What it writes and reads the capture through; the real one when left out.</param>
    /// <param name="output">Where it says what it warns about; nowhere anyone reads, when left out.</param>
    public DeveloperEnvironmentProvider Provider(string processor = "x86_64", IFileSystem? fileSystem = null, IHarnessOutput? output = null)
        => new(
            HostDoubles.Platform(PlatformId.Windows, processor),
            this,
            fileSystem ?? new PhysicalFileSystem(FilePermissionsFactory.Create()),
            output ?? new HarnessFactory().Output);

    /// <summary>What <c>set</c> lists: one <c>NAME=value</c> to a line.</summary>
    private static string Listing(Dictionary<string, string> variables)
        => string.Concat(variables.Select(pair => $"{pair.Key}={pair.Value}\r\n"));
}
