using System.CommandLine;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Runs;

namespace RepoHarness.Cli;

/// <summary>What a machine dispatching a leg tells the host that runs it.</summary>
internal static class DispatchOptions
{
    /// <summary>
    /// The host this machine is to the machine that dispatched a leg here: every selected leg runs on
    /// this machine, with the settings that machine's configuration gives the host it names.
    /// </summary>
    /// <remarks>
    /// Hidden, because nobody types it. One option for every command that runs legs, so no command can
    /// read it differently.
    /// </remarks>
    internal static Option<HostId?> Here { get; } = new(RemoteLegRunner.HereOption)
    {
        Description = "Run every selected leg on this machine, as the host the machine that dispatched it names.",
        Hidden = true,
        CustomParser = result =>
        {
            var spelled = result.Tokens.Count == 1 ? result.Tokens[0].Value : null;

            if (HostId.TryParse(spelled, out var host))
            {
                return host;
            }

            result.AddError(
                $"{RemoteLegRunner.HereOption} names a host as 'local', 'wsl <distribution>' or 'ssh <name>', "
                + $"and '{spelled}' is none of them.");
            return null;
        },
    };
}
