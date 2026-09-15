namespace RepoHarness.Core.Output;

/// <inheritdoc cref="IHarnessOutput"/>
public sealed class ConsoleHarnessOutput(TextWriter standardOutput, TextWriter standardError, bool verbose)
    : IHarnessOutput
{
    private readonly TextWriter _out = standardOutput;
    private readonly TextWriter _error = standardError;
    private readonly Lock _gate = new();

    /// <summary>Writes to the process console.</summary>
    public ConsoleHarnessOutput(bool verbose)
        : this(Console.Out, Console.Error, verbose)
    {
    }

    public bool IsVerbose { get; } = verbose;

    public void Ok(string command, string message) => Write(_out, $"{command}: OK - {message}");

    public void Fail(string command, string message) => Write(_error, $"{command}: FAIL - {message}");

    public void Warn(string command, string message) => Write(_error, $"{command}: WARN - {message}");

    public void Info(string command, string message) => Write(_out, $"{command}: {message}");

    public void Detail(string command, string message)
    {
        if (IsVerbose)
        {
            Write(_out, $"{command}: {message}");
        }
    }

    public void Data(string text) => Write(_out, text);

    public void Raw(string line) => Write(_out, line);

    public void RawError(string line) => Write(_error, line);

    private void Write(TextWriter writer, string line)
    {
        lock (_gate)
        {
            writer.WriteLine(line);
        }
    }
}
