using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Processes;

/// <inheritdoc cref="IProcessRunner"/>
public sealed class ProcessRunner(IHostPlatform platform, IFilePermissions filePermissions) : IProcessRunner
{
    private const int ErrorFileNotFound = 2;

    private const int ReadBufferSize = 4096;

    /// <summary>
    /// How child output is decoded, on every platform. Left unset, Windows decodes
    /// redirected output with the console code page while Linux and macOS use UTF-8, so a
    /// path such as <c>C:\Users\João</c> printed by git, which always writes UTF-8, would
    /// reach the harness garbled on Windows alone.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IHostPlatform _platform = platform;
    private readonly IFilePermissions _filePermissions = filePermissions;

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked before starting. On Linux and macOS a missing working directory fails with
        // the same error number as a missing executable, so it would otherwise be reported
        // as "git is not installed" when git is installed and it is the directory that is gone.
        if (!string.IsNullOrEmpty(request.WorkingDirectory) && !Directory.Exists(request.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Cannot run '{request.FileName}': the working directory '{request.WorkingDirectory}' does not exist.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in request.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(key);
            }
            else
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorFileNotFound)
        {
            throw new ExecutableNotFoundException(request.FileName, ex);
        }

        // Both streams are read from the moment the process starts: a child that fills a
        // pipe nobody is reading blocks forever.
        var standardOutput = CaptureAsync(process.StandardOutput, request.OnOutputLine);
        var standardError = CaptureAsync(process.StandardError, request.OnErrorLine);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is { } budget)
        {
            timeoutSource.CancelAfter(budget);
        }

        var stopped = false;

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Whether for the budget or because the caller gave up, the whole tree goes: a
            // descendant left running keeps the pipes, and whatever it was building, busy.
            stopped = true;
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // The process has exited, but what it wrote just before exiting can still be in the
        // pipes. Reading both streams to their end is what guarantees none of it is lost.
        var capturedOutput = await standardOutput.ConfigureAwait(false);
        var capturedError = await standardError.ConfigureAwait(false);
        stopwatch.Stop();

        if (stopped && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return new ProcessResult(
            ExitCode: stopped ? -1 : process.ExitCode,
            StandardOutput: capturedOutput,
            StandardError: capturedError,
            Duration: stopwatch.Elapsed,
            TimedOut: stopped);
    }

    public string? FindExecutable(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        // An explicit path is used as given rather than searched for.
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            var explicitPath = Path.GetFullPath(command);
            return ProbeCandidates(explicitPath).FirstOrDefault(_filePermissions.IsExecutable);
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidateDirectory;
            try
            {
                // A relative PATH entry is relative to the working directory, exactly as
                // a shell treats it; an entry that is not a path at all is skipped.
                candidateDirectory = Path.GetFullPath(directory.Trim('"'));
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var candidate in ProbeCandidates(Path.Combine(candidateDirectory, command)))
            {
                if (_filePermissions.IsExecutable(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads one stream to its end, keeping the text exactly as the child wrote it, and hands
    /// each complete line to <paramref name="onLine"/> as soon as it arrives.
    /// </summary>
    /// <remarks>
    /// Text rebuilt from lines loses what separated them. git's NUL-separated output would
    /// gain a line break git never wrote, and every line ending would become the host's own,
    /// so one command would capture different text on Windows than on Linux.
    /// </remarks>
    private static async Task<string> CaptureAsync(StreamReader reader, Action<string>? onLine)
    {
        var captured = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[ReadBufferSize];
        int read;

        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            captured.Append(buffer, 0, read);

            if (onLine is null)
            {
                continue;
            }

            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == '\n')
                {
                    onLine(WithoutCarriageReturn(line));
                    line.Clear();
                }
                else
                {
                    line.Append(buffer[index]);
                }
            }
        }

        // A last line with no line break after it is still a line.
        if (onLine is not null && line.Length > 0)
        {
            onLine(WithoutCarriageReturn(line));
        }

        return captured.ToString();
    }

    private static string WithoutCarriageReturn(StringBuilder line)
        => line.Length > 0 && line[^1] == '\r'
            ? line.ToString(0, line.Length - 1)
            : line.ToString();

    /// <summary>
    /// Yields the file names to probe for one candidate location. Windows resolves a
    /// bare name through PATHEXT, so <c>cmake</c> must also be tried as <c>cmake.exe</c>
    /// and <c>cmake.cmd</c>; other platforms use the name exactly as written.
    /// </summary>
    private IEnumerable<string> ProbeCandidates(string basePath)
    {
        if (_platform.Current != PlatformId.Windows)
        {
            yield return basePath;
            yield break;
        }

        if (Path.HasExtension(basePath))
        {
            yield return basePath;
        }

        var extensions = Environment.GetEnvironmentVariable("PATHEXT")
            ?? ".COM;.EXE;.BAT;.CMD";

        foreach (var extension in extensions.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return basePath + extension.Trim();
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
        catch (NotSupportedException)
        {
            // Platform refused a tree kill; the process is already being torn down.
        }
    }
}
