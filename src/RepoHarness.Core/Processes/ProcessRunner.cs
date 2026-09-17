using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Processes;

/// <inheritdoc cref="IProcessRunner"/>
public sealed class ProcessRunner(IHostPlatform platform, IFilePermissions filePermissions) : IProcessRunner
{
    /// <summary>ENOENT on Linux and macOS, ERROR_FILE_NOT_FOUND on Windows.</summary>
    private const int ErrorFileNotFound = 2;

    /// <summary>ERROR_PATH_NOT_FOUND on Windows. On Linux and macOS the same number means something else.</summary>
    private const int WindowsErrorPathNotFound = 3;

    private const int ReadBufferSize = 4096;

    /// <summary>
    /// How child output is decoded, and child input encoded, on every platform. Left unset,
    /// Windows decodes redirected output with the console code page while Linux and macOS use
    /// UTF-8, so a path such as <c>C:\Users\João</c> printed by git, which always writes UTF-8,
    /// would reach the harness garbled on Windows alone.
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
            FileName = ResolveProgram(request.FileName),
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,

            // Every child gets an input of its own, never this process's. A tool that reads its input would
            // otherwise consume what was meant for the harness. And on Windows a child that inherits an input
            // this process is reading at that moment can hang as it starts: the pending read holds the pipe,
            // and one of the first things a runtime such as git's does is ask that pipe what it is.
            RedirectStandardInput = true,
            StandardInputEncoding = Utf8NoBom,
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

            // Before the readers below, so nothing the child says can arrive ahead of the fact that
            // it started.
            request.OnStarted?.Invoke();
        }
        catch (Win32Exception ex)
        {
            throw StartFailure(request.FileName, startInfo.FileName, ex);
        }

        // Both streams are read from the moment the process starts: a child that fills a
        // pipe nobody is reading blocks forever.
        var standardOutput = CaptureAsync(process.StandardOutput, request.OnOutputLine);
        var standardError = CaptureAsync(process.StandardError, request.OnErrorLine);

        // Written while both output streams are being read, so a child that answers as it reads
        // can never block this on a full output pipe, nor this block it on a full input pipe.
        var standardInput = WriteInputAsync(
            process.StandardInput,
            request.StandardInput ?? string.Empty,
            close: !request.HoldStandardInputOpen);

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
        await standardInput.ConfigureAwait(false);

        if (request.HoldStandardInputOpen)
        {
            // Held open while the child ran, so that its end could tell the child this process had
            // gone; closed now that the child has exited.
            CloseQuietly(process.StandardInput);
        }

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

        if (!IsPath(command))
        {
            return OnPath(command);
        }

        // A path is used as given rather than searched for, with the one extension Windows adds to it.
        var path = Path.GetFullPath(_platform.Current == PlatformId.Windows ? WithWindowsExtension(command) : command);
        return _filePermissions.IsExecutable(path) ? path : null;
    }

    /// <summary>
    /// The first file that would start as <paramref name="name"/> in the directories
    /// <paramref name="pathVariable"/> lists, or <see langword="null"/> when there is none. Nowhere else
    /// is looked in.
    /// </summary>
    internal static string? ProgramOnPath(string name, string? pathVariable, bool windows, Func<string, bool> isExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(isExecutable);

        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var fileName = windows ? WithWindowsExtension(name) : name;

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                // A relative PATH entry is relative to the working directory, exactly as a shell treats
                // it; an entry that is not a path at all is skipped. Joined rather than combined, so a name
                // Windows reads as rooted, such as C:tool, cannot step out of the directory.
                candidate = Path.Join(Path.GetFullPath(directory.Trim('"')), fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (isExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The file to start for <paramref name="fileName"/>: a path is used as given, and a name is looked up
    /// in the PATH directories and nowhere else.
    /// </summary>
    /// <remarks>
    /// Left to the runtime, a name is looked for beside the running executable and in the current directory
    /// before PATH: .NET does so on Linux and macOS, and CreateProcess does on Windows. The current directory
    /// is usually the repository the harness was pointed at, so a file named git or dotnet committed at its
    /// root would run in place of the real tool. A relative path is made absolute for the same reason: .NET
    /// would otherwise look for it beside its own executable first.
    /// </remarks>
    /// <exception cref="ExecutableNotFoundException">A name is in none of the PATH directories.</exception>
    private string ResolveProgram(string fileName)
        => IsPath(fileName)
            ? Path.GetFullPath(fileName)
            : OnPath(fileName) ?? throw new ExecutableNotFoundException(fileName);

    private string? OnPath(string name)
        => ProgramOnPath(name, Environment.GetEnvironmentVariable("PATH"), _platform.Current == PlatformId.Windows, _filePermissions.IsExecutable);

    /// <summary>
    /// What a failure to start <paramref name="resolved"/> means. It is reported as not found only when the
    /// file really is missing: the operating system gives the same error for a script whose interpreter, or
    /// a program whose loader, is missing, and "not found" would send the reader after a file that is there.
    /// Anything else, such as a file that is not a program for this machine or may not be run, is reported
    /// with the reason the operating system gave.
    /// </summary>
    private ProgramStartException StartFailure(string requested, string resolved, Win32Exception exception)
    {
        var missing = exception.NativeErrorCode == ErrorFileNotFound
            || (exception.NativeErrorCode == WindowsErrorPathNotFound && _platform.Current == PlatformId.Windows);

        if (!missing)
        {
            return new ProgramStartException(requested, $"'{resolved}' could not be started: {exception.Message}", exception);
        }

        return File.Exists(resolved)
            ? new ProgramStartException(
                requested,
                $"'{resolved}' exists but could not be started: the system reports a file missing, which happens when the interpreter or loader it needs is missing.",
                exception)
            : new ExecutableNotFoundException(requested, exception);
    }

    /// <summary>Whether <paramref name="program"/> names a file by its path rather than by a name to look up.</summary>
    private static bool IsPath(string program)
        => program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar);

    /// <summary>
    /// The file Windows starts for <paramref name="name"/>: the name itself when it has an extension, and
    /// otherwise the name with <c>.exe</c>, the one extension CreateProcess adds. A batch file is never
    /// found for a bare name: cmd.exe would parse its arguments a second time, so they would not arrive as
    /// they were passed.
    /// </summary>
    private static string WithWindowsExtension(string name) => Path.HasExtension(name) ? name : name + ".exe";

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

    /// <summary>
    /// Writes a child's whole input, then closes it when <paramref name="close"/> is set, which is how the
    /// child learns there is no more to read.
    /// </summary>
    private static async Task WriteInputAsync(StreamWriter writer, string input, bool close)
    {
        try
        {
            await writer.WriteAsync(input.AsMemory()).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The child stopped reading, typically by exiting without needing all of it. What it
            // did is reported by its exit code and its output, not by the pipe it left behind.
        }
        finally
        {
            if (close)
            {
                CloseQuietly(writer);
            }
        }
    }

    private static void CloseQuietly(StreamWriter writer)
    {
        try
        {
            writer.Close();
        }
        catch (IOException)
        {
            // Closing flushes, and the pipe can already be gone because the child exited.
        }
    }

    private static string WithoutCarriageReturn(StringBuilder line)
        => line.Length > 0 && line[^1] == '\r'
            ? line.ToString(0, line.Length - 1)
            : line.ToString();

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
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            // A descendant could not be stopped: it was already exiting, or it runs as another user. The
            // process itself must still go, or the wait for it that follows would never end.
            try
            {
                process.Kill();
            }
            catch (Exception inner) when (inner is InvalidOperationException or Win32Exception)
            {
                // It exited meanwhile, or cannot be signalled either; waiting for it says which.
            }
        }
    }
}
