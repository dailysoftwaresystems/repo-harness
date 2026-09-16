using System.CommandLine;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Results;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness check-anchor-citations</c>.</summary>
internal static class CheckAnchorCitationsCommand
{
    internal const string Name = "check-anchor-citations";

    private static readonly Option<bool> CurrentCommitOption = new("--current-commit")
    {
        Description = "Check the files as HEAD holds them. This is the default.",
    };

    private static readonly Option<bool> CurrentTreeOption = new("--current-tree")
    {
        Description = "Check the files on disk, including ones not committed yet.",
    };

    private static readonly Option<bool> CurrentPullRequestOption = new("--current-pr")
    {
        Description = "Check only the files this branch changed against the merge base with the default branch.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write what was scanned and every unresolved citation as JSON.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Fail when an anchor id cited in a scanned root resolves to no row in either registry. The roots come from anchors.citationRoots; nothing outside one is read.");

        command.Options.Add(CurrentCommitOption);
        command.Options.Add(CurrentTreeOption);
        command.Options.Add(CurrentPullRequestOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var report = await context.Get<IAnchorCitationService>()
                .CheckAsync(context.Directory, ReadSubject(arguments), cancellationToken)
                .ConfigureAwait(false);

            return AnchorCitationReports.Render(report, arguments.GetValue(JsonOption));
        }));

        return command;
    }

    /// <summary>
    /// Which subject was asked for. Two of them together are refused rather than ranked: they answer
    /// different questions, and silently checking one of the two would report a verdict about
    /// something the caller did not ask about.
    /// </summary>
    private static AnchorCitationSubject ReadSubject(ParseResult arguments)
    {
        var chosen = new List<(Option<bool> Option, AnchorCitationSubject Subject)>
        {
            (CurrentCommitOption, AnchorCitationSubject.CurrentCommit),
            (CurrentTreeOption, AnchorCitationSubject.CurrentTree),
            (CurrentPullRequestOption, AnchorCitationSubject.CurrentPullRequest),
        }
            .Where(pair => arguments.GetValue(pair.Option))
            .ToList();

        if (chosen.Count > 1)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{string.Join(", ", chosen.Select(pair => pair.Option.Name))} check different things, so only one can be given.");
        }

        return chosen.Count == 1 ? chosen[0].Subject : AnchorCitationSubject.CurrentCommit;
    }
}
