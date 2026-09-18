using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Runs;

/// <summary>
/// Runs one leg on the host it was placed on, by asking the DssHarness already installed there.
/// </summary>
/// <remarks>
/// A leg placed on a WSL distribution or an ssh host has to run there, or its verdict describes the
/// machine that typed the command rather than the machine the leg names — and a green result that
/// describes the wrong machine is the whole failure this tool exists to prevent. So the work is not
/// reproduced over a transport: the host's own DssHarness runs the same command in the copy sync put
/// there, and hands its ledger back as JSON. Both ends are already the same build, which the host
/// inspection established before anything started.
/// The request carries <c>--here</c>, so the host runs the leg on itself and never dispatches it
/// onward. One hop, always, whatever its configuration says about other machines.
/// </remarks>
public sealed class RemoteLegRunner(IHostCommandRunner hostCommands, IHarnessOutput output)
{
    /// <summary>
    /// The option that tells a DssHarness to run every selected leg on the machine it is running on.
    /// </summary>
    /// <remarks>
    /// Hidden, because nobody types it: it exists so the host that was asked to run a leg cannot
    /// decide to ask a third machine, which would place the verdict one further hop from the reader
    /// and could not terminate by construction.
    /// </remarks>
    public const string HereOption = "--here";

    private static readonly JsonSerializerOptions LedgerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;

    /// <summary>Runs <paramref name="commandName"/> for one leg on its host, and returns its entry.</summary>
    /// <param name="commandName">The command to run there, which is the one running here.</param>
    /// <param name="leg">The placed leg, whose host and repository path say where and what.</param>
    /// <param name="repositoryPath">The host's copy of the repository, which sync created.</param>
    /// <param name="arguments">The command's own options, without <c>--legs</c> or <c>--json</c>.</param>
    /// <param name="cancellationToken">Stops the command on the host as well as here.</param>
    /// <exception cref="HarnessException">
    /// The host never reported how the command finished, or reported a ledger this build cannot
    /// read. Neither is a verdict about the code, so neither is reported as one. Or the command
    /// refused the whole run there, which is raised as that refusal, with what the host said.
    /// </exception>
    public async Task<LegEntry> RunAsync(
        string commandName,
        PlacedLeg leg,
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(arguments);

        var session = leg.Host.Session ?? throw new HarnessException(
            HarnessExit.HostUnavailable,
            $"{leg.Host.Host} was not reached, so leg '{leg.Name}' cannot run there: "
            + $"{leg.Host.Reason ?? "no reason was recorded"}.");

        var nonce = HostAgentProtocol.NewNonce();

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,
                Directory = repositoryPath,
                Arguments = [commandName, "--legs", leg.Name, "--json", HereOption, .. arguments],
                Nonce = nonce,
            },
            HostAgentProtocol.JsonOptions);

        var ledger = new System.Text.StringBuilder();
        int? finished = null;
        string? failure = null;

        var result = await _hostCommands.RunAsync(
                session.Connection,
                new HostCommand
                {
                    Program = session.ToolPath,
                    Arguments = _output.IsVerbose
                        ? [HostAgentProtocol.CommandName, HostAgentProtocol.VerboseOption]
                        : [HostAgentProtocol.CommandName],
                    StandardInput = request + "\n",
                    HoldStandardInputOpen = true,

                    // Kept rather than echoed: the host writes its ledger to standard output, and
                    // this end reports one ledger for the whole run rather than one per machine.
                    OnOutputLine = line => ledger.AppendLine(line),
                    OnErrorLine = line =>
                    {
                        if (HostAgentProtocol.TryReadCompletionLine(line, nonce, out var code))
                        {
                            finished = code;
                        }
                        else
                        {
                            // Kept as well as shown: a command that refuses before any leg has a
                            // verdict leaves no ledger, and this line is then all it said about why.
                            if (FailureLine.TryRead(line, commandName, out var said))
                            {
                                failure = said;
                            }

                            _output.RawError(line);
                        }
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (finished is null)
        {
            // Never a failed verdict: the command may not have run, or run only in part, and
            // reporting that as a red leg would blame the code for a connection.
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{leg.Host.Host}: '{commandName}' for leg '{leg.Name}' never reported how it "
                + $"finished, so it may not have run, or run only in part; the connection ended with "
                + $"exit {result.ExitCode}{Detail(result.StandardError)}");
        }

        return Read(ledger.ToString(), leg, commandName, finished.Value, failure);
    }

    /// <summary>Reads the one leg's entry out of the ledger the host wrote.</summary>
    /// <remarks>
    /// The whole of standard output is the document. The host was asked with <c>--json</c>, which
    /// sends its progress to standard error precisely so that nothing shares this channel: hunting
    /// for the first <c>{</c> instead would find the one inside a compiler message or a test name
    /// that a progress line had already carried here, and report a leg that reached a verdict as a
    /// host that could not be reached.
    /// </remarks>
    /// <param name="output">Everything the host wrote to standard output.</param>
    /// <param name="leg">The leg asked for.</param>
    /// <param name="commandName">The command the host ran.</param>
    /// <param name="exitCode">How that command finished.</param>
    /// <param name="failure">What its failure line said, when it wrote one.</param>
    /// <exception cref="HarnessException">
    /// The host wrote no entry for the leg. A refusal of the whole run there is raised as that same
    /// refusal; anything else as a host that said nothing about the leg.
    /// </exception>
    private static LegEntry Read(string output, PlacedLeg leg, string commandName, int exitCode, string? failure)
    {
        var document = output.Trim();

        RemoteLedger? ledger = null;

        if (document.Length > 0)
        {
            try
            {
                ledger = JsonSerializer.Deserialize<RemoteLedger>(document, LedgerOptions);
            }
            catch (JsonException ex)
            {
                throw new HarnessException(
                    HarnessExit.HostUnavailable,
                    $"{leg.Host.Host} answered '{commandName}' with a ledger this build cannot read: {ex.Message}");
            }
        }

        var entry = ledger?.Legs?.FirstOrDefault();

        if (entry is null)
        {
            // A refusal the host made is this run's refusal. A configuration, a command line or a
            // policy its copy cannot satisfy is the same fact there as here, and the same refusal on
            // this machine stops the run; read as a host that could not be reached, it became a
            // skipped leg and a run that merely looked incomplete, with its reason and its fix left
            // behind on the host's error stream.
            if (HarnessExit.RefusesTheRun(exitCode))
            {
                throw new HarnessException(
                    exitCode,
                    $"{leg.Host.Host} refused '{commandName}' for leg '{leg.Name}': "
                    + (failure ?? $"it exited {exitCode} and said nothing more"));
            }

            // The exit code is named because it may be the only thing the host did say. A copy with
            // no configuration, a leg that cannot be placed there, a tool that refused before it
            // began: each ends with its own code and no ledger, and without the code every one of
            // them reads as the same shrug.
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{leg.Host.Host} ran '{commandName}' for leg '{leg.Name}' and exited {exitCode} without "
                + "a ledger entry for it, "
                + (failure is null ? "so nothing there said what happened." : $"saying: {failure}"));
        }

        return new LegEntry
        {
            Leg = leg.Name,
            Verdict = Verdicts.Parse(entry.Verdict) ?? LegVerdict.Poisoned,
            Detail = entry.Detail ?? string.Empty,
            Duration = TimeSpan.FromSeconds(entry.DurationSeconds),
            CommandTime = TimeSpan.FromSeconds(entry.CommandSeconds),
            Emulated = leg.Emulated,
            TestCount = entry.TestCount,
            TimingNotes = [.. entry.TimingNotes ?? []],
        };
    }

    private static string Detail(string standardError)
    {
        var said = HostProbes.Excerpt(standardError);

        return said.Length == 0 ? string.Empty : ": " + said;
    }

    /// <summary>The shape a host's ledger arrives in, read back by name rather than by position.</summary>
    private sealed record RemoteLedger(
        [property: JsonPropertyName("legs")] IReadOnlyList<RemoteLedgerLeg>? Legs);

    /// <summary>One leg's line of a host's ledger.</summary>
    private sealed record RemoteLedgerLeg(
        string? Verdict,
        string? Detail,
        double DurationSeconds,
        double CommandSeconds,
        int? TestCount,
        IReadOnlyList<string>? TimingNotes);
}
