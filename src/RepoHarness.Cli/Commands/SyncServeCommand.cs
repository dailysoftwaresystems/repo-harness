using System.CommandLine;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Cli.Commands;

/// <summary>
/// Wires the hidden <c>DssHarness sync-serve</c>, which a machine runs on a host to act on that
/// host's copy of the repository.
/// </summary>
/// <remarks>
/// Hidden because nobody types it: it is the far side of a sync, and every operation it performs is
/// one the machine asking already decided on. It serves them through the same
/// <see cref="LocalSyncTransport"/> this machine uses for a copy of its own, so there is one
/// implementation of what a sync does to a tree and no second one to drift from it.
/// </remarks>
internal static class SyncServeCommand
{
    internal const string Name = SyncServe.CommandName;

    private static readonly Argument<string> OperationArgument = new("operation")
    {
        Description = "The operation to perform on this host's copy.",
    };

    private static readonly Argument<string[]> ArgumentsArgument = new("arguments")
    {
        Description = "The operation's arguments: the copy's root, then whatever the operation takes.",
        Arity = ArgumentArity.OneOrMore,
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Serve one sync operation on this host's copy of the repository. Not meant to be typed; the machine syncing runs it.")
        {
            Hidden = true,
        };

        command.Arguments.Add(OperationArgument);
        command.Arguments.Add(ArgumentsArgument);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var operation = context.ParseResult.GetRequiredValue(OperationArgument);
            var arguments = context.ParseResult.GetRequiredValue(ArgumentsArgument);
            var transport = context.Get<ISyncTransport>();
            var root = arguments[0];

            switch (operation)
            {
                case SyncServe.Inspect:
                    return Answer(new SyncInspectAnswer(
                        await transport.RootExistsAsync(root, cancellationToken).ConfigureAwait(false),
                        await transport.ReadMarkAsync(root, cancellationToken).ConfigureAwait(false)));

                case SyncServe.Create:
                    await transport
                        .CreateRootAsync(root, SyncServe.MarkIn(arguments), cancellationToken)
                        .ConfigureAwait(false);

                    return Done();

                case SyncServe.InitRepository:
                    await transport.InitialiseRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
                    return Done();

                case SyncServe.Manifest:
                    var withheld = Required(arguments, 1, operation)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                    var manifest = await transport
                        .ReadManifestAsync(root, withheld, cancellationToken)
                        .ConfigureAwait(false);

                    return Answer(new SyncManifestAnswer([.. manifest.Paths.Select(path => manifest.Entries[path])])
                    {
                        Links = manifest.Links,
                    });

                case SyncServe.Write:
                    await transport
                        .WriteFileAsync(
                            root,
                            Required(arguments, 1, operation),
                            Convert.FromBase64String(Required(arguments, 2, operation)),
                            cancellationToken)
                        .ConfigureAwait(false);

                    return Done();

                case SyncServe.Delete:
                    await transport
                        .DeleteFileAsync(root, Required(arguments, 1, operation), cancellationToken)
                        .ConfigureAwait(false);

                    return Done();

                case SyncServe.Read:
                    var contents = await transport
                        .ReadFileAsync(root, Required(arguments, 1, operation), cancellationToken)
                        .ConfigureAwait(false);

                    // Hashed here, where the bytes were read. A hash the asking machine took of what
                    // arrived would agree with those bytes whatever happened on the way.
                    return Answer(new SyncFileAnswer(
                        Convert.ToBase64String(contents),
                        RepoHarness.Core.FileSystem.FileContentHash.Of(contents)));

                default:
                    // Named rather than passed over: an operation this build does not know means the
                    // two ends are different builds, which the version check should already have
                    // refused, so reporting it as "nothing happened" would hide a real defect.
                    throw new HarnessException(
                        HarnessExit.UsageError,
                        $"'{operation}' is not a sync operation this build serves.");
            }
        }));

        return command;
    }

    /// <summary>An answer the machine syncing reads, written to standard output on its own line.</summary>
    private static CommandOutcome Answer<T>(T answer)
        => CommandOutcome.Ok(string.Empty) with { Data = [SyncServe.Answer(answer)], Quiet = true };

    /// <summary>An operation that answers nothing, which must still say nothing rather than a status line.</summary>
    private static CommandOutcome Done() => CommandOutcome.Ok(string.Empty) with { Quiet = true };

    private static string Required(string[] arguments, int index, string operation)
        => index < arguments.Length
            ? arguments[index]
            : throw new HarnessException(
                HarnessExit.UsageError,
                $"sync operation '{operation}' needs {index + 1} argument(s); it was given {arguments.Length}.");
}
