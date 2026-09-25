using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RepoHarness.Core.Processes;

/// <summary>Starts this tool again as a process of its own, which goes on after the one starting it has ended.</summary>
public interface IDetachedProcessLauncher
{
    /// <summary>Starts this tool with <paramref name="arguments"/>, detached: never waited for, and reading and writing nothing of this process.</summary>
    /// <param name="arguments">Its command line.</param>
    /// <exception cref="ProgramStartException">It could not be started.</exception>
    void StartSelf(IReadOnlyList<string> arguments);
}

/// <inheritdoc cref="IDetachedProcessLauncher"/>
/// <remarks>
/// The one process this tool starts and does not wait for. Its standard streams are its own, never this
/// process's: the connection a host serves a request over ends when every holder of its streams has closed
/// them, and a child holding them would keep the machine that asked waiting for as long as the child ran.
/// A command run over ssh has no terminal, so nothing is hung up on the child as the connection ends; on a
/// Windows host OpenSSH may still end the processes of a session with it.
/// On Windows a child started with streams of its own still inherits every handle this process lets be
/// inherited, this process's standard streams among them, so there it is started inheriting none.
/// </remarks>
public sealed class DetachedProcessLauncher : IDetachedProcessLauncher
{
    /// <summary>A console of its own, with no window.</summary>
    private const uint CreateNoWindow = 0x08000000;

    /// <summary>A process group of its own, which an interruption typed where it was started does not reach.</summary>
    private const uint CreateNewProcessGroup = 0x00000200;

    public void StartSelf(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var (program, prefix) = Self();

        if (OperatingSystem.IsWindows())
        {
            StartInheritingNothing(program, [.. prefix, .. arguments]);
            return;
        }

        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // Somewhere that stays: the directory a request was served in can be a copy removed later.
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var argument in prefix.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)
                ?? throw new ProgramStartException(program, $"'{program}' did not start.");

            // Its input ends at once: it reads none.
            process.StandardInput.Close();
        }
        catch (Win32Exception ex)
        {
            throw new ProgramStartException(program, $"'{program}' could not be started: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The program that is this tool, and what it is given before its own command line: nothing for the tool's
    /// own executable, and the tool's assembly for a runtime hosting it, as a build from source runs.
    /// </summary>
    private static (string Program, IReadOnlyList<string> Prefix) Self()
    {
        var program = Environment.ProcessPath ?? throw new ProgramStartException("dssharness", "this process does not know the program it runs as.");

        return string.Equals(Path.GetFileNameWithoutExtension(program), "dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } assembly
            ? (program, ["exec", assembly])
            : (program, []);
    }

    /// <summary>
    /// Starts <paramref name="program"/> with <paramref name="arguments"/>, inheriting no handle of this process's,
    /// in a console of its own with no window, and in the same place a start elsewhere gives it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void StartInheritingNothing(string program, IReadOnlyList<string> arguments)
    {
        // A path holds no quote, so the program's name is quoted as it is: it is read back by other rules than the
        // arguments after it.
        var commandLine = $"\"{program}\" {CommandLine(arguments)}";
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };

        if (!CreateProcessW(
                program,
                [.. commandLine, '\0'],
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                CreateNoWindow | CreateNewProcessGroup,
                IntPtr.Zero,
                AppContext.BaseDirectory,
                ref startup,
                out var started))
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new ProgramStartException(program, $"'{program}' could not be started: {error.Message}", error);
        }

        // Never waited for: what names it here is let go at once, and it runs on.
        new SafeProcessHandle(started.Process, ownsHandle: true).Dispose();
        new SafeWaitHandle(started.Thread, ownsHandle: true).Dispose();
    }

    /// <summary>
    /// <paramref name="arguments"/> written as a Windows command line, from which the program started reads each back
    /// as it is: quoted where it is empty or holds white space or a quote, with each quote, and the backslashes
    /// before it or before the closing quote, escaped.
    /// </summary>
    internal static string CommandLine(IEnumerable<string> arguments)
    {
        var commandLine = new StringBuilder();

        foreach (var argument in arguments)
        {
            if (commandLine.Length > 0)
            {
                commandLine.Append(' ');
            }

            if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
            {
                commandLine.Append(argument);
                continue;
            }

            commandLine.Append('"');
            var backslashes = 0;

            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                commandLine.Append('\\', character == '"' ? (backslashes * 2) + 1 : backslashes).Append(character);
                backslashes = 0;
            }

            commandLine.Append('\\', backslashes * 2).Append('"');
        }

        return commandLine.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

#pragma warning disable CS0649 // Read and written by Windows, through the call, which the compiler does not see.

    /// <summary>STARTUPINFOW: nothing asked of the new process's window or streams beyond its own console.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int Columns;
        public int Rows;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedBytes;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    /// <summary>PROCESS_INFORMATION: the new process and its first thread.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

#pragma warning restore CS0649
}
