namespace RepoHarness.Core.Output;

/// <inheritdoc cref="IHarnessOutput"/>
public sealed class ConsoleHarnessOutput(TextWriter standardOutput, TextWriter standardError, bool verbose)
    : IHarnessOutput
{
    private readonly TextWriter _out = standardOutput;
    private readonly TextWriter _error = standardError;
    private readonly Lock _gate = new();
    private bool _dataOnly;

    /// <summary>Writes to the process console.</summary>
    public ConsoleHarnessOutput(bool verbose)
        : this(Console.Out, Console.Error, verbose)
    {
    }

    public bool IsVerbose { get; } = verbose;

    public void Ok(string command, string message) => Write(Progress, $"{command}: OK - {message}");

    public void Fail(string command, string message) => Write(_error, $"{command}: FAIL - {message}");

    public void Warn(string command, string message) => Write(_error, $"{command}: WARN - {message}");

    public void Info(string command, string message) => Write(Progress, $"{command}: {message}");

    public void Detail(string command, string message)
    {
        if (IsVerbose)
        {
            Write(Progress, $"{command}: {message}");
        }
    }

    public void Data(string text) => Write(_out, text);

    public void Raw(string line) => Write(Progress, line);

    public void RawError(string line) => Write(_error, line);

    public IDisposable DataOnly()
    {
        lock (_gate)
        {
            _dataOnly = true;
        }

        return new DataOnlyScope(this);
    }

    /// <summary>
    /// Where anything that is not the command's answer goes. Standard error while a document is
    /// being written, so the document is the only thing on standard output.
    /// </summary>
    private TextWriter Progress
    {
        get
        {
            lock (_gate)
            {
                return _dataOnly ? _error : _out;
            }
        }
    }

    private void Write(TextWriter writer, string line)
    {
        lock (_gate)
        {
            writer.WriteLine(line);
        }
    }

    private sealed class DataOnlyScope(ConsoleHarnessOutput output) : IDisposable
    {
        private readonly ConsoleHarnessOutput _output = output;

        public void Dispose()
        {
            lock (_output._gate)
            {
                _output._dataOnly = false;
            }
        }
    }
}
