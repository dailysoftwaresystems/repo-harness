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

    /// <summary>
    /// The variable a program is looked up on. Spelled once: on Windows the environment a child is
    /// given compares names without case, so this also finds the 'Path' Windows itself writes.
    /// </summary>
    private const string PathVariable = "PATH";

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

        if (request.AppendToPath.Count > 0)
        {
            var current = startInfo.Environment.TryGetValue(PathVariable, out var inherited) ? inherited : null;

            startInfo.Environment[PathVariable] = string.Join(
                Path.PathSeparator,
                new[] { current }.Concat(request.AppendToPath).Where(part => !string.IsNullOrEmpty(part)));
        }

        // Looked up on the PATH the child is given, not on this process's own. The two used to differ
        // whenever a request changed PATH, and then the program started was one the child's own
        // lookups could not see: cmake resolved from one PATH and the ninja it starts from another.
        startInfo.FileName = ResolveProgram(
            request.FileName,
            startInfo.Environment.TryGetValue(PathVariable, out var effective) ? effective : null);

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

        var path = ProgramAtPath(command, _platform.Current == PlatformId.Windows);
        return _filePermissions.IsExecutable(path) ? path : null;
    }

    /// <summary>
    /// Whether <paramref name="program"/> names a file by its path rather than a program to look up by
    /// name.
    /// </summary>
    /// <remarks>
    /// The one rule, read by everything that has to tell the two apart: the run that starts a program,
    /// the survey that looks for one first, and the policy that decides whether an action may name
    /// it. Either separator counts on every platform, so a configuration written on one machine means
    /// the same thing on the next; and so does a name Windows reads as rooted, such as <c>C:tool</c>,
    /// which is no name a program is installed under.
    /// </remarks>
    /// <param name="program">The program as a configuration or a request names it.</param>
    internal static bool IsPath(string program)
        => program.Contains('/', StringComparison.Ordinal)
            || program.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(program);

    /// <summary>
    /// The file a program named by its path is: made absolute, with the one extension Windows adds.
    /// </summary>
    /// <remarks>
    /// One spelling for the run that starts it and for every question asked about it beforehand, so a
    /// path a survey found is the file the run starts rather than a sibling without its extension.
    /// </remarks>
    /// <param name="program">A program named by its path.</param>
    /// <param name="windows">Whether the file is started on Windows.</param>
    /// <exception cref="ArgumentException">The path is not one this machine can express.</exception>
    internal static string ProgramAtPath(string program, bool windows)
        => Path.GetFullPath(windows ? WithWindowsExtension(program) : program);

    /// <summary>
    /// <paramref name="program"/> with a relative path read against <paramref name="directory"/>; a
    /// name, or a path that is already full, as given.
    /// </summary>
    /// <remarks>
    /// Left to the start, a relative path is read against whatever directory this process happens to
    /// be in: the main checkout for a leg that works in a worktree, and a subdirectory when the command
    /// was typed in one. Either way the file started is some other tree's, under the same name.
    /// </remarks>
    /// <param name="program">The program as a configuration names it.</param>
    /// <param name="directory">The directory a relative path is written from.</param>
    internal static string Anchored(string program, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return IsPath(program) && !Path.IsPathFullyQualified(program)
            ? Path.GetFullPath(program, directory)
            : program;
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

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ProgramInDirectory(name, directory.Trim('"'), windows, isExecutable) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The file that would start as <paramref name="name"/> in <paramref name="directory"/>, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// The one rule for turning a name into a candidate file, used for every PATH entry and for every
    /// directory searched beyond the PATH, so a program found in one place is found the same way in
    /// the other. A relative directory is relative to the working directory, exactly as a shell treats
    /// a PATH entry; one that is not a path at all holds nothing. Joined rather than combined, so a
    /// name Windows reads as rooted, such as C:tool, cannot step out of the directory.
    /// </remarks>
    internal static string? ProgramInDirectory(string name, string directory, bool windows, Func<string, bool> isExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(isExecutable);

        string candidate;

        try
        {
            candidate = Path.Join(Path.GetFullPath(directory), windows ? WithWindowsExtension(name) : name);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return isExecutable(candidate) ? candidate : null;
    }

    /// <summary>
    /// The file to start for <paramref name="fileName"/>: a path is the file it names, and a name is looked
    /// up in the PATH directories and nowhere else.
    /// </summary>
    /// <remarks>
    /// Left to the runtime, a name is looked for beside the running executable and in the current directory
    /// before PATH: .NET does so on Linux and macOS, and CreateProcess does on Windows. The current directory
    /// is usually the repository the harness was pointed at, so a file named git or dotnet committed at its
    /// root would run in place of the real tool. A relative path is made absolute for the same reason: .NET
    /// would otherwise look for it beside its own executable first.
    /// </remarks>
    /// <param name="fileName">The program as the request names it.</param>
    /// <param name="pathVariable">The PATH the child is given.</param>
    /// <exception cref="ExecutableNotFoundException">A name is in none of the PATH directories.</exception>
    private string ResolveProgram(string fileName, string? pathVariable)
        => IsPath(fileName)
            ? ProgramAtPath(fileName, _platform.Current == PlatformId.Windows)
            : ProgramOnPath(fileName, pathVariable, _platform.Current == PlatformId.Windows, _filePermissions.IsExecutable)
                ?? throw new ExecutableNotFoundException(fileName);

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

    /// <summary>
    /// The file Windows starts for <paramref name="name"/>: the name itself when it has an extension, and
    /// otherwise the name with <c>.exe</c>, the one extension CreateProcess adds. A batch file is never
    /// found for a bare name: cmd.exe would parse its arguments a second time, so they would not arrive as
    /// they were passed.
    /// </summary>
    internal static string WithWindowsExtension(string name) => Path.HasExtension(name) ? name : name + ".exe";

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
