namespace RepoHarness.Core.Output;

/// <summary>
/// Console output. Every line the harness itself emits identifies the command that
/// produced it, so output interleaved from nested or remote work stays attributable.
/// Passthrough output from child processes is deliberately unprefixed.
/// </summary>
public interface IHarnessOutput
{
    /// <summary>Whether verbose detail is being shown.</summary>
    bool IsVerbose { get; }

    /// <summary>Reports success: <c>&lt;command&gt;: OK - &lt;message&gt;</c>.</summary>
    void Ok(string command, string message);

    /// <summary>Reports failure to stderr: <c>&lt;command&gt;: FAIL - &lt;message&gt;</c>.</summary>
    void Fail(string command, string message);

    /// <summary>Reports a non fatal problem: <c>&lt;command&gt;: WARN - &lt;message&gt;</c>.</summary>
    void Warn(string command, string message);

    /// <summary>Reports progress that is worth seeing by default.</summary>
    void Info(string command, string message);

    /// <summary>Reports detail shown only when verbose.</summary>
    void Detail(string command, string message);

    /// <summary>
    /// Writes a command's result to stdout, unprefixed: a listing, a detail block, a JSON document.
    /// Unprefixed so that another program can read what a command answered without stripping anything.
    /// </summary>
    void Data(string text);

    /// <summary>Writes a line of passthrough output from a child process, unprefixed.</summary>
    void Raw(string line);

    /// <summary>Writes a line of passthrough stderr from a child process, unprefixed.</summary>
    void RawError(string line);
}
