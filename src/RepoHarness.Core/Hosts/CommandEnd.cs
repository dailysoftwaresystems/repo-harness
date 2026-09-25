namespace RepoHarness.Core.Hosts;

/// <summary>What every command says and leaves on the hosts it reached as it ends, however it ended.</summary>
/// <remarks>
/// One place, called once by what runs every command: that a synced copy could not reach a host is said once,
/// last, and each host that asks to be held awake between commands is held.
/// </remarks>
/// <param name="copyRefusals">Whether a host was refused from a synced copy.</param>
/// <param name="holds">The hosts reached that ask to be held awake between commands.</param>
public sealed class CommandEnd(SyncedCopyRefusals copyRefusals, HoldAwakeRegistry holds)
{
    private readonly SyncedCopyRefusals _copyRefusals = copyRefusals;
    private readonly HoldAwakeRegistry _holds = holds;

    /// <summary>Says what the command leaves to say, then leaves each hold.</summary>
    /// <param name="commandName">The command ending, which prefixes what is said.</param>
    /// <remarks>
    /// Asked apart from the command's own token, so an interrupted command still leaves its holds; each host's
    /// hold is bounded by its own budget.
    /// </remarks>
    public Task EndAsync(string commandName)
    {
        _copyRefusals.SayOnce(commandName);

        return _holds.LeaveHoldsAsync(commandName, CancellationToken.None);
    }
}
