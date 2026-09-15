using System.CommandLine;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Results;

namespace RepoHarness.Cli.Commands;

/// <summary>Reads the --pending and --done options the anchor commands share.</summary>
internal static class AnchorScopeOptions
{
    internal static Option<bool> Pending(string description) => new("--pending") { Description = description };

    internal static Option<bool> Done(string description) => new("--done") { Description = description };

    internal static AnchorScope Read(ParseResult parseResult, Option<bool> pending, Option<bool> done)
    {
        var onlyPending = parseResult.GetValue(pending);
        var onlyDone = parseResult.GetValue(done);

        if (onlyPending && onlyDone)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                "--pending and --done cannot be combined; leave both off to use both registries.");
        }

        return onlyPending ? AnchorScope.Pending : onlyDone ? AnchorScope.Done : AnchorScope.All;
    }
}

/// <summary>Wires <c>repo-harness write-anchor</c>.</summary>
internal static class WriteAnchorCommand
{
    internal const string Name = "write-anchor";

    private static readonly Argument<string> IdArgument = new("id")
    {
        Description = "The new anchor's id, for example D-AREA-TOPIC-DETAIL.",
    };

    private static readonly Option<string> PriorityOption = new("--priority")
    {
        Description = "P0, the most urgent, to P5.",
        Required = true,
    };

    private static readonly Option<string> TriggerOption = new("--trigger")
    {
        Description = "What is wrong, and what would make it worth doing now.",
        Required = true,
    };

    private static readonly Option<string> StatusOption = new("--status")
    {
        Description = "open, gated, disclosed or closed. A closed anchor is filed in the done registry.",
        DefaultValueFactory = _ => "open",
    };

    private static readonly Option<string?> ClosingOption = new("--closing")
    {
        Description = "What remains to be done to close it.",
    };

    private static readonly Option<string?> CrossRefsOption = new("--cross-refs")
    {
        Description = "Where it is cited, and related anchors.",
    };

    private static readonly Option<bool> DryRunOption = new("--anchor-dry-run")
    {
        Description = "Show the anchor and where it would be filed, and write nothing.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Add a new anchor: to the pending registry, or to the done registry when its status is closed.");

        command.Arguments.Add(IdArgument);
        command.Options.Add(PriorityOption);
        command.Options.Add(TriggerOption);
        command.Options.Add(StatusOption);
        command.Options.Add(ClosingOption);
        command.Options.Add(CrossRefsOption);
        command.Options.Add(DryRunOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var request = new AnchorWriteRequest(
                arguments.GetRequiredValue(IdArgument),
                arguments.GetRequiredValue(PriorityOption),
                arguments.GetRequiredValue(TriggerOption))
            {
                Status = arguments.GetValue(StatusOption) ?? "open",
                ClosingWork = arguments.GetValue(ClosingOption),
                CrossRefs = arguments.GetValue(CrossRefsOption),
            };

            var change = await context.Get<IAnchorRegistryService>()
                .WriteAsync(context.Directory, request, arguments.GetValue(DryRunOption), cancellationToken)
                .ConfigureAwait(false);

            return AnchorReports.Change(change);
        }));

        return command;
    }
}

/// <summary>Wires <c>repo-harness set-anchor</c>.</summary>
internal static class SetAnchorCommand
{
    internal const string Name = "set-anchor";

    private static readonly Argument<string> IdArgument = new("id")
    {
        Description = "The anchor to change.",
    };

    private static readonly Option<bool> PendingOption = AnchorScopeOptions.Pending("Look for the anchor in the pending registry only.");

    private static readonly Option<bool> DoneOption = AnchorScopeOptions.Done("Look for the anchor in the done registry only.");

    private static readonly Option<string?> PriorityOption = new("--priority")
    {
        Description = "A new priority: P0, the most urgent, to P5.",
    };

    private static readonly Option<string?> StatusOption = new("--status")
    {
        Description = "A new status: open, gated, disclosed or closed. Closing an anchor moves it to the done registry; any other status moves it back to pending.",
    };

    private static readonly Option<string?> TriggerOption = new("--trigger")
    {
        Description = "Replace the Trigger cell.",
    };

    private static readonly Option<string?> ClosingOption = new("--closing")
    {
        Description = "Replace the Closing work cell.",
    };

    private static readonly Option<string?> CrossRefsOption = new("--cross-refs")
    {
        Description = "Replace the Cross-refs cell.",
    };

    private static readonly Option<bool> DryRunOption = new("--anchor-dry-run")
    {
        Description = "Show the change, and write nothing.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Change an existing anchor. Changing its status moves it between the pending and done registries.");

        command.Arguments.Add(IdArgument);
        command.Options.Add(PendingOption);
        command.Options.Add(DoneOption);
        command.Options.Add(PriorityOption);
        command.Options.Add(StatusOption);
        command.Options.Add(TriggerOption);
        command.Options.Add(ClosingOption);
        command.Options.Add(CrossRefsOption);
        command.Options.Add(DryRunOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var request = new AnchorSetRequest(arguments.GetRequiredValue(IdArgument))
            {
                Scope = AnchorScopeOptions.Read(arguments, PendingOption, DoneOption),
                Priority = arguments.GetValue(PriorityOption),
                Status = arguments.GetValue(StatusOption),
                Trigger = arguments.GetValue(TriggerOption),
                ClosingWork = arguments.GetValue(ClosingOption),
                CrossRefs = arguments.GetValue(CrossRefsOption),
            };

            var change = await context.Get<IAnchorRegistryService>()
                .SetAsync(context.Directory, request, arguments.GetValue(DryRunOption), cancellationToken)
                .ConfigureAwait(false);

            return AnchorReports.Change(change);
        }));

        return command;
    }
}

/// <summary>Wires <c>repo-harness read-anchor</c>.</summary>
internal static class ReadAnchorCommand
{
    internal const string Name = "read-anchor";

    private static readonly Argument<string[]> IdsArgument = new("ids")
    {
        Description = "One or more anchor ids, matched exactly.",
        Arity = ArgumentArity.OneOrMore,
    };

    private static readonly Option<bool> PendingOption = AnchorScopeOptions.Pending("Look in the pending registry only.");

    private static readonly Option<bool> DoneOption = AnchorScopeOptions.Done("Look in the done registry only.");

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the anchors found as a JSON array.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Show anchors in full by id. Ids not found are listed, and the exit code is then 1.");

        command.Arguments.Add(IdsArgument);
        command.Options.Add(PendingOption);
        command.Options.Add(DoneOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var lookup = await context.Get<IAnchorRegistryService>()
                .ReadAsync(
                    context.Directory,
                    arguments.GetRequiredValue(IdsArgument),
                    AnchorScopeOptions.Read(arguments, PendingOption, DoneOption),
                    cancellationToken)
                .ConfigureAwait(false);

            return AnchorReports.Lookup(lookup, arguments.GetValue(JsonOption));
        }));

        return command;
    }
}

/// <summary>Wires <c>repo-harness read-anchors</c>.</summary>
internal static class ReadAnchorsCommand
{
    internal const string Name = "read-anchors";

    private static readonly Option<bool> PendingOption = AnchorScopeOptions.Pending("List the pending registry only.");

    private static readonly Option<bool> DoneOption = AnchorScopeOptions.Done("List the done registry only.");

    private static readonly Option<string[]> BandOption = new("--band")
    {
        Description = "Only these priorities, for example --band P0 P1.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> OpenOption = new("--open")
    {
        Description = "Only anchors that are not closed.",
    };

    private static readonly Option<bool> ClosedOption = new("--closed")
    {
        Description = "Only closed anchors.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the list, or the lint findings, as JSON.",
    };

    private static readonly Option<bool> LintOption = new("--lint")
    {
        Description = "Check both registries in full instead of listing: malformed rows, misfiled anchors and duplicate ids. The exit code is 1 when there are problems. Takes none of the listing filters.",
    };

    /// <summary>The options that only narrow a listing.</summary>
    private static readonly Option[] ListingFilters = [PendingOption, DoneOption, BandOption, OpenOption, ClosedOption];

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "List anchors by priority, status and id, or check the registries with --lint.");

        command.Options.Add(PendingOption);
        command.Options.Add(DoneOption);
        command.Options.Add(BandOption);
        command.Options.Add(OpenOption);
        command.Options.Add(ClosedOption);
        command.Options.Add(JsonOption);
        command.Options.Add(LintOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;
            var service = context.Get<IAnchorRegistryService>();
            var json = arguments.GetValue(JsonOption);

            if (arguments.GetValue(LintOption))
            {
                RefuseListingFilters(arguments);

                var findings = await service.LintAsync(context.Directory, cancellationToken).ConfigureAwait(false);
                return AnchorReports.Lint(findings, json);
            }

            var filter = new AnchorListFilter
            {
                Scope = AnchorScopeOptions.Read(arguments, PendingOption, DoneOption),
                Bands = arguments.GetValue(BandOption) ?? [],
                OnlyOpen = arguments.GetValue(OpenOption),
                OnlyClosed = arguments.GetValue(ClosedOption),
            };

            var entries = await service.ListAsync(context.Directory, filter, cancellationToken).ConfigureAwait(false);
            return AnchorReports.List(entries, filter.Scope, json);
        }));

        return command;
    }

    /// <summary>
    /// Refuses a listing filter given with --lint. Lint checks both registries in full, because a
    /// duplicate id spans them, so a filter beside it would be silently ignored.
    /// </summary>
    private static void RefuseListingFilters(ParseResult arguments)
    {
        var given = ListingFilters
            .Where(option => arguments.GetResult(option) is { Implicit: false })
            .Select(option => option.Name)
            .ToList();

        if (given.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"--lint checks both registries in full, so it cannot be combined with {string.Join(", ", given)}.");
        }
    }
}

/// <summary>Wires <c>repo-harness check-anchor-balance</c>.</summary>
internal static class CheckAnchorBalanceCommand
{
    internal const string Name = "check-anchor-balance";

    private static readonly Option<string> BaseOption = new("--base")
    {
        Description = "The commit to compare with.",
        DefaultValueFactory = _ => AnchorBalanceService.DefaultBase,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the receipt as JSON.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Fail when a change leaves more open anchors than it found, or a registry is misfiled or malformed.");

        command.Options.Add(BaseOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            var report = await context.Get<IAnchorBalanceService>()
                .CheckAsync(context.Directory, arguments.GetValue(BaseOption), cancellationToken)
                .ConfigureAwait(false);

            return AnchorReports.Balance(report, arguments.GetValue(JsonOption));
        }));

        return command;
    }
}
