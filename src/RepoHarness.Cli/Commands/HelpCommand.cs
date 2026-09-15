using System.CommandLine;
using System.Text;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Cli.Commands;

/// <summary>
/// Wires <c>repo-harness help</c>: reference material that does not fit in option
/// descriptions. <c>--help</c> answers "what can I type"; this answers "what do the
/// answers mean", which is what a caller reaching for documentation actually wants.
/// </summary>
internal static class HelpCommand
{
    internal const string Name = "help";

    /// <summary>Opening words of the unrecognised-topic message.</summary>
    internal const string UnknownTopicPrefix = "Unknown topic";

    private static readonly Argument<string?> TopicArgument = new("topic")
    {
        Description = "Topic to explain: exit-codes, config, worktrees, anchors, layout. Omit for an overview.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    internal static Command Create()
    {
        var command = new Command(Name, "Explain exit codes, configuration, worktrees, anchors and layout.");
        command.Arguments.Add(TopicArgument);
        GlobalOptions.AddTo(command);

        command.SetAction(parseResult =>
        {
            var topic = parseResult.GetValue(TopicArgument);

            if (!TryRender(topic, out var text))
            {
                // A mistyped topic is a usage error, and it goes to stderr: zero always
                // means success, and stdout stays pipeable.
                Console.Error.Write(text);
                return HarnessExit.UsageError;
            }

            Console.Out.Write(text);
            return HarnessExit.Success;
        });

        return command;
    }

    /// <summary>Renders one topic, reporting whether it was recognised.</summary>
    internal static bool TryRender(string? topic, out string text)
    {
        text = Render(topic);
        return !text.StartsWith(UnknownTopicPrefix, StringComparison.Ordinal);
    }

    /// <summary>Renders one topic. Separated from the command so it is directly testable.</summary>
    internal static string Render(string? topic) => (topic?.ToLowerInvariant()) switch
    {
        "exit-codes" or "exit" => RenderExitCodes(),
        "config" or "configuration" => RenderConfig(),
        "worktrees" or "worktree" => RenderWorktrees(),
        "anchors" or "anchor" => RenderAnchors(),
        "layout" => RenderLayout(),
        null or "" => RenderOverview(),
        _ => $"{UnknownTopicPrefix} '{topic}'. Try: exit-codes, config, worktrees, anchors, layout.{Environment.NewLine}",
    };

    private static string RenderOverview()
    {
        var builder = new StringBuilder();

        builder.AppendLine("repo-harness - one cross-platform tool for a repository's build, test and");
        builder.AppendLine("cross-host work. Every behaviour is declared in .harness-config/config.json;");
        builder.AppendLine("nothing about a specific repository or toolchain is built into the tool.");
        builder.AppendLine();
        builder.AppendLine("Getting started");
        builder.AppendLine("  repo-harness verify-git            Check git is present and this is a repository");
        builder.AppendLine("  repo-harness init                  Create .harness-config and seed config.json");
        builder.AppendLine("  repo-harness list-worktree         Show existing worktrees");
        builder.AppendLine("  repo-harness read-anchors          List the deferred work recorded as anchors");
        builder.AppendLine();
        builder.AppendLine("Every command accepts");
        builder.AppendLine("  -C, --directory <dir>              Operate on this directory (default: current)");
        builder.AppendLine("  -v, --verbose                      Show per-phase detail and child process output");
        builder.AppendLine();
        builder.AppendLine("Topics");
        builder.AppendLine("  repo-harness help exit-codes       What each exit code means");
        builder.AppendLine("  repo-harness help config           What config.json declares");
        builder.AppendLine("  repo-harness help worktrees        Naming rules and the path budget");
        builder.AppendLine("  repo-harness help anchors          Anchor registries and the commands that change them");
        builder.AppendLine("  repo-harness help layout           What init creates, and what git tracks");
        builder.AppendLine();
        builder.AppendLine("Use 'repo-harness <command> --help' for a command's own options.");

        return builder.ToString();
    }

    private static string RenderExitCodes()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Exit codes");
        builder.AppendLine();
        builder.AppendLine("Zero always means success. A command that failed and a harness that could not");
        builder.AppendLine("run never share a code, because the remedies differ.");
        builder.AppendLine();
        builder.AppendLine("Shared by every command:");

        foreach (var description in HarnessExit.All)
        {
            builder.AppendLine($"  {description.Code,3}  {description.Name,-16} {description.Explanation}");
        }

        builder.AppendLine();
        builder.AppendLine("Codes 1-9 are reserved for a command's own contract:");
        builder.AppendLine();
        builder.AppendLine("  verify-git");

        foreach (var status in Enum.GetValues<VerifyGitStatus>())
        {
            builder.AppendLine($"    {(int)status,3}  {status}");
        }

        builder.AppendLine();
        builder.AppendLine("  read-anchor, read-anchors --lint, check-anchor-balance");
        builder.AppendLine($"    {AnchorExit.Findings,3}  an id was not found, the registries have problems, or the balance did not hold");

        return builder.ToString();
    }

    private static string RenderWorktrees()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Worktrees");
        builder.AppendLine();
        builder.AppendLine("Names are lowercase letters and digits joined by single hyphens, at most");
        builder.AppendLine($"{WorktreeSettings.DefaultMaxNameLength} characters unless worktrees.maxNameLength says otherwise.");
        builder.AppendLine("Use --random to have one generated.");
        builder.AppendLine();
        builder.AppendLine("The length limit is not cosmetic. A worktree's build tree sits below");
        builder.AppendLine("  .harness-config/worktrees/<name>");
        builder.AppendLine("and on Windows that path plus the longest path the build system generates");
        builder.AppendLine($"beneath it must stay under {HostPlatform.WindowsMaxPath} characters. Exceeding it does not fail as");
        builder.AppendLine("a path error: it appears as compile errors in files the worktree never touched.");
        builder.AppendLine();
        builder.AppendLine("create-worktree refuses up front when the budget cannot be met, and says how");
        builder.AppendLine("long a name would still fit. Both sides of the budget are in config.json:");
        builder.AppendLine("  worktrees.pathBudgetReserve   the longest path your build generates in a tree");
        builder.AppendLine("  worktrees.pathLimit           replaces the platform limit; set it only when");
        builder.AppendLine("                                every tool in the build handles long paths");
        builder.AppendLine();
        builder.AppendLine("Worktrees always belong to the main checkout, so running create-worktree from");
        builder.AppendLine("inside a worktree adds a sibling rather than nesting one.");

        return builder.ToString();
    }

    private static string RenderAnchors()
    {
        var defaults = new AnchorSettings();
        var rules = AnchorIdRules.From(defaults);
        var builder = new StringBuilder();

        builder.AppendLine("Anchors");
        builder.AppendLine();
        builder.AppendLine("An anchor is a named piece of deferred work, kept as one row of a markdown table.");
        builder.AppendLine("Two registries hold every anchor, and each anchor lives in exactly one of them:");
        builder.AppendLine();
        builder.AppendLine($"  pending   anchors.pendingAnchorsPath (default {defaults.PendingAnchorsPath})");
        builder.AppendLine("            open, gated and disclosed anchors: the work that is left");
        builder.AppendLine($"  done      anchors.doneAnchorsPath (default {defaults.DoneAnchorsPath})");
        builder.AppendLine("            closed anchors: the archive");
        builder.AppendLine();
        builder.AppendLine("init creates each registry that is missing, from a skeleton holding an");
        builder.AppendLine("introduction and an empty table, and never touches one that exists. Every");
        builder.AppendLine("command, init included, finds a registry the same way: one git tracks is in the");
        builder.AppendLine("tree the command runs in, so a change travels with that branch, and one git");
        builder.AppendLine("ignores is in the main checkout.");
        builder.AppendLine();
        builder.AppendLine("Statuses. The Status cell is the only verdict a row carries:");
        builder.AppendLine($"  {AnchorStatus.Render(AnchorState.Open)}       live work that can be picked up now");
        builder.AppendLine($"  {AnchorStatus.Render(AnchorState.Gated)}      live work waiting on a trigger; when it fires, set it to open");
        builder.AppendLine($"  {AnchorStatus.Render(AnchorState.Disclosed)}  live debt that existed before anyone wrote it down");
        builder.AppendLine($"  {AnchorStatus.Render(AnchorState.Closed)}     finished");
        builder.AppendLine();
        builder.AppendLine("A row is closed exactly when its Status cell starts with the closed mark. Closing");
        builder.AppendLine("an anchor moves its row to the done registry, and any other status moves it back.");
        builder.AppendLine("A row is added at the end of its table: rows are never sorted.");
        builder.AppendLine();
        builder.AppendLine($"Rows: {AnchorRegistryDocument.TableHeader}");
        builder.AppendLine($"  Priority runs from {AnchorPriority.Bands[0]}, the most urgent, to {AnchorPriority.Bands[^1]}.");
        builder.AppendLine("  A new id is anchors.idPrefix, then at least anchors.minimumIdSegments");
        builder.AppendLine($"  hyphen-separated segments, the first in capitals: {rules.Example()} with the");
        builder.AppendLine($"  defaults ({defaults.IdPrefix} and {defaults.MinimumIdSegments}). Ids already in a registry are never re-checked.");
        builder.AppendLine();
        builder.AppendLine("A registry is an introduction and exactly one anchor table. A file with no anchor");
        builder.AppendLine("table, a second one, or an anchor row outside that table is malformed. Every");
        builder.AppendLine("command that reads or changes anchors refuses it rather than risk a miscount;");
        builder.AppendLine("read-anchors --lint shows what to repair.");
        builder.AppendLine();
        builder.AppendLine("Commands");
        builder.AppendLine("  write-anchor ID --priority P --trigger TEXT [--status S] [--closing TEXT]");
        builder.AppendLine("               [--cross-refs TEXT]                  add a new anchor");
        builder.AppendLine("  set-anchor ID [--priority] [--status] [--trigger] [--closing] [--cross-refs]");
        builder.AppendLine("                                                    change an existing anchor");
        builder.AppendLine("  read-anchor ID [ID ...] [--json]                  show anchors in full");
        builder.AppendLine("  read-anchors [--band P ...] [--open|--closed] [--json]");
        builder.AppendLine("                                                    list anchors");
        builder.AppendLine("  read-anchors --lint [--json]                      check both registries");
        builder.AppendLine("  check-anchor-balance [--base REF] [--json]        compare with a commit");
        builder.AppendLine();
        builder.AppendLine("--pending or --done limits set-anchor, read-anchor and a read-anchors listing to");
        builder.AppendLine("one registry; read-anchors --lint always checks both, and takes no filter.");
        builder.AppendLine("write-anchor and set-anchor write immediately; --anchor-dry-run shows the change");
        builder.AppendLine("and writes nothing. Pass values as you mean them: pipes are escaped and line breaks");
        builder.AppendLine("collapse for you, and a pipe you already escaped is refused.");
        builder.AppendLine();
        builder.AppendLine($"check-anchor-balance compares the working tree with --base (default {AnchorBalanceService.DefaultBase}), by id");
        builder.AppendLine("across both registries, so moving a row counts as nothing. It fails when open");
        builder.AppendLine("anchors rose, not counting anchors newly disclosed, and when a closed anchor is in");
        builder.AppendLine("the pending registry, a live one is in the done registry, or a registry is");
        builder.AppendLine("malformed. A registry that did not exist at the base counts as empty there; one git");
        builder.AppendLine("ignores has no history, and is refused.");
        builder.AppendLine();
        builder.AppendLine("Every change holds a machine-wide lock on its two registries. A change that cannot");
        builder.AppendLine($"take it within {NamedMutexAnchorRegistryLock.DefaultTimeout.TotalSeconds:0} seconds writes nothing and exits {HarnessExit.Refused}.");
        builder.AppendLine();
        builder.AppendLine("Exit codes");
        builder.AppendLine($"  {AnchorExit.Findings,3}  an id was not found, --lint found problems, or the balance did not hold");
        builder.AppendLine($"  {HarnessExit.UsageError,3}  a value is not valid (an id, a priority, a status, an empty trigger),");
        builder.AppendLine("       options that cannot be combined, or set-anchor with nothing to change");
        builder.AppendLine($"  {HarnessExit.NotInitialized,3}  a registry is missing; run 'repo-harness init'");
        builder.AppendLine($"  {HarnessExit.Refused,3}  refused: the id exists, there is no such anchor, an id has two rows,");
        builder.AppendLine("       the registry is ignored by git, or the lock is held");
        builder.AppendLine($"  {HarnessExit.CommandFailed,3}  a registry is malformed, or the base commit cannot be read");
        builder.AppendLine();
        builder.AppendLine("read-anchors --lint and check-anchor-balance report a missing or malformed registry");
        builder.AppendLine($"as one of their findings instead, so for them it exits {AnchorExit.Findings}.");

        return builder.ToString();
    }

    private static string RenderLayout()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Layout created by init");
        builder.AppendLine();
        builder.AppendLine("  .harness-config/config.json        tracked by git; the whole contract");
        builder.AppendLine("  .harness-config/worktrees/         contents ignored, .gitkeep tracked");
        builder.AppendLine("  .harness-config/ssh/               contents ignored, .gitkeep tracked");
        builder.AppendLine("  .harness-config/lock.json          ignored; records in-progress runs");
        builder.AppendLine($"  {AnchorSettings.DefaultPendingAnchorsPath}");
        builder.AppendLine("                                     tracked; live anchors (anchors.pendingAnchorsPath)");
        builder.AppendLine($"  {AnchorSettings.DefaultDoneAnchorsPath}");
        builder.AppendLine("                                     tracked; closed anchors (anchors.doneAnchorsPath)");
        builder.AppendLine();
        builder.AppendLine("init creates each anchor registry that is missing, from a skeleton holding an");
        builder.AppendLine("introduction and an empty table, and never touches one that exists. A registry");
        builder.AppendLine("git tracks belongs to the branch, so it is created in the tree init runs in.");
        builder.AppendLine();
        builder.AppendLine("init adds these rules to .gitignore inside a marked block, replacing that");
        builder.AppendLine("block on later runs and leaving every other rule untouched.");
        builder.AppendLine();
        builder.AppendLine("Ignored state lives only in the main checkout. A worktree receives the tracked");
        builder.AppendLine("part of .harness-config through git but never the ignored part, so ssh secrets");
        builder.AppendLine("and the run lock resolve back to the originating checkout.");

        return builder.ToString();
    }

    private static string RenderConfig()
    {
        var builder = new StringBuilder();

        builder.AppendLine("config.json");
        builder.AppendLine();
        builder.AppendLine($"  defaults       buildCores and testCores ({HarnessDefaults.DefaultCores} each), maxParallelLegs, default");
        builder.AppendLine("                 project and leg set, stall bound");
        builder.AppendLine("  toolchains     compilers, as environment and cache variables (msvc, gcc, clang)");
        builder.AppendLine("  sanitizers     instrumentation overlays composed onto a build");
        builder.AppendLine("  buildConfigs   named configurations (debug, release, o1, o2)");
        builder.AppendLine("  projects       what to build, and with which adapter (cmake, dotnet, dart)");
        builder.AppendLine("  targets        machines to reach, by transport (local, wsl, ssh), each with");
        builder.AppendLine("                 its own core counts when they differ");
        builder.AppendLine("  legs           units of work: target + project + toolchain + config (+ sanitizer)");
        builder.AppendLine("  legSets        named groups of legs, so a gate runs under one name");
        builder.AppendLine("  tools          external tools to verify and install");
        builder.AppendLine("  runners        multi-phase procedures such as a corpus test or a benchmark");
        builder.AppendLine("  exec           named commands to run through 'repo-harness exec'");
        builder.AppendLine("  commit         commit template and sign-off policy");
        builder.AppendLine("  sync           what the tree mirror carries, and what it must never carry");
        builder.AppendLine("  contention     tools that, running against a leg's build directory, void its result");
        builder.AppendLine("  worktrees      naming, path budget and path limit");
        builder.AppendLine("  anchors        the pending and done anchor registries, and how new ids are spelled");
        builder.AppendLine();
        builder.AppendLine("A toolchain, a build config and a sanitizer overlay compose: each contributes");
        builder.AppendLine("environment and cache variables, so clang x debug x asan needs no entry of its");
        builder.AppendLine("own. Each combination builds in its own directory, keyed by that combination,");
        builder.AppendLine("so two toolchains never share one build tree.");
        builder.AppendLine();
        builder.AppendLine("Selected legs run at the same time, and a command waits for all of them. Within");
        builder.AppendLine("a leg the order is fixed: sync when the target needs it, then build on buildCores");
        builder.AppendLine("cores, then test on testCores cores.");
        builder.AppendLine();
        builder.AppendLine("A verdict must describe the code, not the moment it ran in. Every test invocation");
        builder.AppendLine("declares a successPattern, since exiting 0 is not proof anything ran. A leg whose");
        builder.AppendLine("test inputs change while it runs, or whose build directory another process uses,");
        builder.AppendLine("gets no pass or fail at all. Durations that diverge between legs, or that span a");
        builder.AppendLine("clock step or a host sleep, are marked suspect, and never change a verdict.");
        builder.AppendLine();
        builder.AppendLine("The file is checked when it is read: unknown keys and references to undeclared");
        builder.AppendLine("names are rejected, with every problem listed at once. Comments and trailing");
        builder.AppendLine("commas are accepted, since the file is meant to be edited. A section whose");
        builder.AppendLine("command is not implemented yet is still checked, but has no effect until that");
        builder.AppendLine("command arrives.");

        return builder.ToString();
    }
}
