namespace RepoHarness.Core.Hosts;

/// <summary>
/// Reads the two streams of one request to a host's agent: which lines are the agent's, and the exit code its
/// completion line carries.
/// </summary>
/// <remarks>
/// A login shell writes to the same streams the agent does, and whatever it writes arrives first: one
/// consumer's printed the account's home layout on every session, which a relayed line then published. So
/// nothing a stream carried before the agent's started line is the request's, except on standard error the
/// agent's own lines, because a request refused before it could be read carries no nonce to mark, and that
/// refusal is the whole of what the reader has to go on. The completion line is read before that gate and
/// never behind it: it is the one thing that must survive a host whose profile writes to that stream, because
/// a line that never arrives is reported as a request that may not have run at all.
/// </remarks>
/// <param name="nonce">The request's nonce.</param>
public sealed class HostAgentLines(string nonce)
{
    private readonly string _nonce = nonce;

    // Whether the agent has begun answering, on each stream.
    private bool _serving;
    private bool _reporting;

    /// <summary>
    /// The exit code the agent's completion line carried, or <see langword="null"/> while none has arrived: the
    /// request may not have run, or run only in part.
    /// </summary>
    public int? Finished { get; private set; }

    /// <summary>Whether <paramref name="line"/>, from standard output, is the agent's.</summary>
    /// <param name="line">A line the host wrote to standard output.</param>
    public bool Output(string line)
    {
        if (HostAgentProtocol.IsStartedLine(line, _nonce))
        {
            _reporting = true;
            return false;
        }

        return _reporting;
    }

    /// <summary>
    /// Whether <paramref name="line"/>, from standard error, is the agent's to relay; its started and completion
    /// lines are read here, and are never relayed.
    /// </summary>
    /// <param name="line">A line the host wrote to standard error.</param>
    public bool Error(string line)
    {
        if (HostAgentProtocol.TryReadCompletionLine(line, _nonce, out var code))
        {
            Finished = code;
            return false;
        }

        if (HostAgentProtocol.IsStartedLine(line, _nonce))
        {
            _serving = true;
            return false;
        }

        return _serving || HostAgentProtocol.IsAgentsOwnLine(line);
    }
}
