using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Platform;

/// <summary>One process the machine was running when the table was read.</summary>
/// <param name="Id">Its process id.</param>
/// <param name="ParentId">The id of the process that started it, where the platform reports one.</param>
/// <param name="Name">The program's name, without a directory or an extension.</param>
/// <param name="StartedUtc">
/// When it started, where the platform reports it. Recorded beside the id because ids are recycled:
/// on Windows a freed id was measured coming back after about a hundred allocations, and without the
/// start time a new process inherits whatever the harness believed about the old one.
/// </param>
/// <param name="CommandLine">
/// Its command line, where the platform exposes one. A process that does not expose it cannot be
/// matched against a build directory, which the report states rather than passing over.
/// </param>
public sealed record SampledProcess(int Id, int? ParentId, string Name, DateTimeOffset? StartedUtc, string? CommandLine);

/// <summary>Reading the machine's process table.</summary>
/// <remarks>
/// A seam for the same reason file permissions are one: every platform answers this question, and
/// each answers it its own way — WMI on Windows, <c>/proc</c> on Linux, <c>ps</c> on macOS. Behind
/// the seam, so nothing above it observes the operating system, and so a test can say what the
/// machine was running without the machine having to be running it.
/// </remarks>
public interface IProcessTable
{
    /// <summary>
    /// Everything running on this machine, and why the reading is less than it should be when it is.
    /// </summary>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <remarks>
    /// Machine-wide, never limited to this process's tree: the contender a leg has to notice is one
    /// somebody started by hand, and that is in nobody's tree but their own.
    /// </remarks>
    Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>One reading of the machine's process table, and what it could not establish.</summary>
/// <param name="Processes">What was running, as far as this reading could tell.</param>
/// <param name="Degraded">
/// Why this reading cannot answer the question it is taken for, or <see langword="null"/> where it
/// can.
/// </param>
/// <remarks>
/// Carried rather than swallowed. The platform's own source is the only one on any of these systems
/// that reports a command line, and a command line is the whole of what contention is decided by:
/// fall back to what the runtime alone can see and every process matches nothing, so a machine
/// where the query is blocked reports "no contender" for every leg, for ever, without a word.
/// "Nobody looked" and "nothing was found" are different facts.
/// </remarks>
public sealed record ProcessTableReading(IReadOnlyList<SampledProcess> Processes, string? Degraded);

/// <summary>Builds the reader for the machine this is running on.</summary>
/// <remarks>
/// One of the few places that observes the operating system, alongside <see cref="HostPlatform"/>
/// and <see cref="FilePermissionsFactory"/>. Everything above it depends on
/// <see cref="IProcessTable"/> and never asks which system it is on.
/// </remarks>
public static class ProcessTableFactory
{
    /// <summary>The reader for this machine.</summary>
    /// <param name="platform">Says which machine this is.</param>
    /// <param name="processRunner">Starts the program a platform has to be asked through.</param>
    public static IProcessTable Create(IHostPlatform platform, IProcessRunner processRunner)
        => new ProcessTable(platform, processRunner);
}

/// <inheritdoc cref="IProcessTable"/>
public sealed class ProcessTable(IHostPlatform platform, IProcessRunner processRunner) : IProcessTable
{
    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;

    /// <summary>Why a platform query failed, in one line, with what it said.</summary>
    private static string HostFailure(string what, ProcessResult result)
    {
        var said = result.StandardError.Trim();

        return said.Length == 0
            ? $"{what} (it exited {result.ExitCode})"
            : $"{what} (it exited {result.ExitCode}: {said.Split(Environment.NewLine)[0]})";
    }

    /// <summary>
    /// What the Windows query separates its fields with: the unit separator, which no process name
    /// or command line contains. Written as a code point rather than typed in as the byte, because a
    /// source file holding a control character is one git and grep read as binary.
    /// </summary>
    private const char UnitSeparator = (char)31;

    /// <inheritdoc/>
    public async Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var (table, problem) = _platform.Current switch
        {
            PlatformId.Windows => await WindowsTableAsync(cancellationToken).ConfigureAwait(false),
            PlatformId.Linux => LinuxTable(),
            _ => await MacTableAsync(cancellationToken).ConfigureAwait(false),
        };

        if (table.Count > 0)
        {
            return new ProcessTableReading(table, null);
        }

        // The fallback still places a tool on the machine by name, which is worth keeping; what it
        // cannot do is say what that tool was pointed at, and that is what decides contention. So it
        // is returned with the reason attached rather than in place of one.
        return new ProcessTableReading(
            FromDiagnostics(),
            problem ?? "the process table came back empty, which no running machine's is");
    }


    /// <summary>
    /// The table from WMI, which is the only source on Windows that carries a command line. Asked
    /// through PowerShell because reading WMI in process would add a dependency the tool does not
    /// otherwise need, and the query is sent base64-encoded so no layer of quoting can change it.
    /// </summary>
    private async Task<(IReadOnlyList<SampledProcess> Table, string? Problem)> WindowsTableAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $s = [char]31
            Get-CimInstance Win32_Process | ForEach-Object {
                $created = ''
                if ($_.CreationDate) { $created = $_.CreationDate.ToUniversalTime().ToString('o') }
                $line = $_.CommandLine
                if ($line) { $line = $line -replace '[\r\n]+', ' ' } else { $line = '' }
                [string]::Join($s, @([string]$_.ProcessId, [string]$_.ParentProcessId, $created, [string]$_.Name, $line))
            }
            """;

        var result = await _processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = "powershell",
                Arguments =
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                ],
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return ([], HostFailure("the process table could not be read from WMI through PowerShell", result));
        }

        var processes = new List<SampledProcess>();

        foreach (var line in Lines(result.StandardOutput))
        {
            var fields = line.Split(UnitSeparator);

            if (fields.Length < 5 || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            processes.Add(new SampledProcess(
                id,
                int.TryParse(fields[1], CultureInfo.InvariantCulture, out var parent) ? parent : null,
                CleanName(fields[3]),
                DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var started) ? started : null,
                fields[4].Length == 0 ? null : fields[4]));
        }

        return (processes, null);
    }

    /// <summary>
    /// The table from <c>/proc</c>, read directly: the kernel already publishes every field needed,
    /// so no other program has to be installed for a leg to know what else is running.
    /// </summary>
    private static (IReadOnlyList<SampledProcess> Table, string? Problem) LinuxTable()
    {
        var processes = new List<SampledProcess>();

        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));

                var fields = ProcStat.FieldsAfterName(stat);
                if (fields is null)
                {
                    continue;
                }

                var name = ProcStat.Name(stat) ?? id.ToString(CultureInfo.InvariantCulture);

                int? parent = ProcStat.Number(fields, ProcStat.ParentField) is { } ppid ? (int)ppid : null;

                // Ticks since boot, counted from the epoch rather than from a boot time worked out
                // from the clock as it is now. What this is for is telling one process from the next
                // holder of its id between two samples, and a boot time derived from the wall clock
                // moves for every live process the moment that clock steps — which fragments one
                // contender into two and makes a report say it came and went when it never left.
                DateTimeOffset? started = ProcStat.Number(fields, ProcStat.StartTicksField) is { } ticks
                    ? DateTimeOffset.UnixEpoch.AddSeconds(ticks / ProcStat.TicksPerSecond)
                    : null;

                var commandLine = ReadCommandLine(Path.Combine(directory, "cmdline"));

                processes.Add(new SampledProcess(id, parent, CleanName(name), started, commandLine));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The process exited between the listing and the read, which is ordinary at this
                // rate of change; the other entries in this sample are still what was running.
            }
        }

        return (processes, null);
    }

    /// <summary>
    /// The table from <c>ps</c>, which is where macOS publishes another process's command line.
    /// <c>lstart</c> rather than elapsed time, because an identity must not shift between samples.
    /// </summary>
    private async Task<(IReadOnlyList<SampledProcess> Table, string? Problem)> MacTableAsync(CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = "ps",
                Arguments = ["-A", "-ww", "-o", "pid=,ppid=,lstart=,args="],

                // C, so the day and month names are the ones the format below reads. A machine set
                // to another language would otherwise leave every start time unknown.
                Environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["LC_ALL"] = "C" },
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return ([], HostFailure("the process table could not be read from ps", result));
        }

        var processes = new List<SampledProcess>();

        foreach (var line in Lines(result.StandardOutput))
        {
            // pid, ppid and the five words of lstart are fixed; everything after them is the
            // command line, spaces and all.
            var (fields, commandLine) = SplitLeading(line, 7);

            if (fields.Count < 7 || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var started = DateTimeOffset.TryParseExact(
                string.Join(' ', fields.Skip(2).Take(5)),
                ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
                out var startedAt)
                ? startedAt
                : (DateTimeOffset?)null;

            processes.Add(new SampledProcess(
                id,
                int.TryParse(fields[1], CultureInfo.InvariantCulture, out var parent) ? parent : null,
                CleanName(FirstToken(commandLine)),
                started,
                commandLine.Length == 0 ? null : commandLine));
        }

        return (processes, null);
    }

    /// <summary>
    /// What the runtime alone can see: ids, names, and a start time for each process this user may
    /// open. Used only when the platform's own source failed, and reported with the limit it carries.
    /// </summary>
    private static IReadOnlyList<SampledProcess> FromDiagnostics()
    {
        var processes = new List<SampledProcess>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                DateTimeOffset? started = null;

                try
                {
                    started = new DateTimeOffset(process.StartTime).ToUniversalTime();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // This user may not open it, or it has already exited. Its id alone is still a
                    // fact, and an unknown start time is never merged with another sample's entry.
                }

                processes.Add(new SampledProcess(process.Id, null, CleanName(process.ProcessName), started, null));
            }
            catch (InvalidOperationException)
            {
                // It exited while the list was being built.
            }
            finally
            {
                process.Dispose();
            }
        }

        return processes;
    }

    private static DateTimeOffset? LinuxBootTime()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("btime ", StringComparison.Ordinal)
                    && long.TryParse(line[6..].Trim(), CultureInfo.InvariantCulture, out var seconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the boot time no start time can be computed, so every process in this sample
            // carries an unknown one and is never merged across samples.
        }

        return null;
    }

    private static string? ReadCommandLine(string path)
    {
        try
        {
            // NUL-separated arguments, exactly as the process was started with, which is what makes
            // this readable at all: no shell has re-quoted it.
            var raw = File.ReadAllText(path);
            var line = raw.Replace('\0', ' ').Trim();
            return line.Length == 0 ? null : line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Lines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);

    /// <summary>The first <paramref name="count"/> whitespace-separated tokens, and the rest of the line unsplit.</summary>
    private static (IReadOnlyList<string> Fields, string Remainder) SplitLeading(string line, int count)
    {
        var fields = new List<string>(count);
        var index = 0;

        while (fields.Count < count && index < line.Length)
        {
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            var start = index;

            while (index < line.Length && line[index] != ' ')
            {
                index++;
            }

            if (index > start)
            {
                fields.Add(line[start..index]);
            }
        }

        return (fields, line[Math.Min(index, line.Length)..].Trim());
    }

    private static string FirstToken(string commandLine)
    {
        var space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? commandLine : commandLine[..space];
    }

    /// <summary>A program's name without its directory or extension, which is how tools are configured.</summary>
    private static string CleanName(string name)
        => name.Length == 0 ? string.Empty : Path.GetFileNameWithoutExtension(name.Trim());
}
