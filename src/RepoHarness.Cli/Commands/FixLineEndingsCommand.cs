using System.CommandLine;
using RepoHarness.Core.LineEndings;
using RepoHarness.Core.Results;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness fix-line-endings</c>.</summary>
internal static class FixLineEndingsCommand
{
    internal const string Name = "fix-line-endings";

    private static readonly Option<bool> AllOption = new("--all")
    {
        Description = "Cover every file git tracks.",
    };

    private static readonly Option<bool> ChangedOption = new("--changed")
    {
        Description = "Cover the working set: what is staged, and what is changed and not staged.",
    };

    private static readonly Option<bool> CheckOption = new("--check")
    {
        Description = "Refuse instead of rewriting, and list what does not match. Nothing is written.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write what was covered and what changed as JSON.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Rewrite files to the line endings .gitattributes declares for them, read through 'git check-attr'. The policy is never restated here, so one statement of it governs both what git stores and what this writes.");

        command.Options.Add(AllOption);
        command.Options.Add(ChangedOption);
        command.Options.Add(CheckOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var request = new LineEndingRequest(ReadScope(arguments), arguments.GetValue(CheckOption));

            var report = await context.Get<ILineEndingService>()
                .ApplyAsync(context.Directory, request, cancellationToken)
                .ConfigureAwait(false);

            return LineEndingReports.Render(report, arguments.GetValue(JsonOption));
        }));

        return command;
    }

    /// <summary>
    /// Which files to cover. Neither option is a default: this command rewrites files, and which
    /// files it rewrites is not something to be guessed on the caller's behalf.
    /// </summary>
    private static LineEndingScope ReadScope(ParseResult arguments)
    {
        var all = arguments.GetValue(AllOption);
        var changed = arguments.GetValue(ChangedOption);

        if (all == changed)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                all
                    ? $"{AllOption.Name} and {ChangedOption.Name} cover different files, so only one can be given."
                    : $"Give {AllOption.Name} for every tracked file, or {ChangedOption.Name} for the working set.");
        }

        return all ? LineEndingScope.All : LineEndingScope.Changed;
    }
}
