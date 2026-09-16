using System.CommandLine;
using System.Text;
using RepoHarness.Core.Ci;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Tools;

namespace RepoHarness.Cli.Commands;

/// <summary>
/// Wires <c>DssHarness help</c>: reference material that does not fit in option
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
        Description = "Topic to explain: exit-codes, config, legs, worktrees, anchors, layout, secrets, tools, runners, verdicts. Omit for an overview.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    internal static Command Create()
    {
        var command = new Command(Name, "Explain exit codes, configuration, legs and hosts, worktrees, anchors, layout, connection data, tools, runners and leg verdicts.");
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
        "legs" or "hosts" or "emulators" => RenderLegs(),
        "worktrees" or "worktree" => RenderWorktrees(),
        "anchors" or "anchor" => RenderAnchors(),
        "layout" => RenderLayout(),
        "secrets" or "hosts-secrets" => RenderSecrets(),
        "tools" => RenderTools(),
        "runners" or "runner" or "actions" => RenderRunners(),
        "verdicts" or "verdict" => RenderVerdicts(),
        null or "" => RenderOverview(),
        _ => $"{UnknownTopicPrefix} '{topic}'. Try: exit-codes, config, legs, worktrees, anchors, layout, "
            + $"secrets, tools, runners, verdicts.{Environment.NewLine}",
    };

    private static string RenderTools()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Installing what a host is missing");
        builder.AppendLine();
        builder.AppendLine("'install-missing-tools' runs over every declared leg, or those --legs names, on the");
        builder.AppendLine("host each leg names with wsl or ssh, and on this machine otherwise. init calls it");
        builder.AppendLine("too, so a fresh clone is ready to run rather than ready to be told what is missing.");
        builder.AppendLine();
        builder.AppendLine("Every WSL distribution and ssh host gets the .NET 10 SDK when it has none, under");
        builder.AppendLine("the home directory, where no login-free PATH names it - which is why every command");
        builder.AppendLine("the harness runs there spells the resolved absolute path rather than a bare name.");
        builder.AppendLine();
        builder.AppendLine("Each entry under tools that carries an install is probed with probe.args and");
        builder.AppendLine("probe.regex, compared against minVersion, and installed or updated through it. An");
        builder.AppendLine("entry with no install is an allowlist entry: probed if it declares a probe,");
        builder.AppendLine("reported when missing, never installed. That is how a program shipping with the");
        builder.AppendLine("platform, or with the repository, is allowed to appear in a runner's steps.");
        builder.AppendLine();
        builder.AppendLine("A privileged install takes its credential from that host's own item, on standard");
        builder.AppendLine("input only: it reaches no argument list, no log and no error message. A second run");
        builder.AppendLine("reports 'already current' and changes nothing. An unreachable host is named, and");
        builder.AppendLine("the other legs still go ahead.");

        return builder.ToString();
    }

    private static string RenderSecrets()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Where each host's connection data lives");
        builder.AppendLine();
        builder.AppendLine("config.json declares only NAMES, under sshItems and wslDistros. An address, a");
        builder.AppendLine("user, a key path and a credential are never in it: that file is tracked, and a");
        builder.AppendLine("config.json arriving through git must not be able to point the harness at a");
        builder.AppendLine("machine nobody set up here.");
        builder.AppendLine();
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemEnvFileName}");
        builder.AppendLine("                                     ADDRESS=, USER=, PORT= (optional)");
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemKeyFileName}");
        builder.AppendLine("                                     the private key");
        builder.AppendLine($"  .harness-config/{HarnessLayout.SshItemsDirectoryName}/<name>/{HarnessLayout.ItemKnownHostsFileName}");
        builder.AppendLine("                                     the host keys ssh may accept");
        builder.AppendLine($"  .harness-config/{HarnessLayout.WslDistrosDirectoryName}/<name>/{HarnessLayout.ItemEnvFileName}");
        builder.AppendLine("                                     the distribution, and the superuser credential");
        builder.AppendLine("                                     install-missing-tools needs");
        builder.AppendLine();
        builder.AppendLine("hosts.ssh and hosts.wsl are keyed by these names, and a host declared without an");
        builder.AppendLine("item is refused when config.json is read: it could never be reached, and failing");
        builder.AppendLine("later would blame ssh for a mistake in this file.");
        builder.AppendLine();
        builder.AppendLine("An .env other users can change is refused before anything connects, because");
        builder.AppendLine("whoever can change it can send the harness somewhere else. A key other users can");
        builder.AppendLine("read is refused because ssh ignores it. Both refusals name the command that fixes");
        builder.AppendLine("them. A credential is passed to a privileged command on standard input, and");
        builder.AppendLine("reaches no log, no argument list and no error message.");
        builder.AppendLine();
        builder.AppendLine("init writes the ignore rules that keep all of it out of git. A fresh clone plus");
        builder.AppendLine("this tree is enough for 'legs' to reach every host.");

        return builder.ToString();
    }

    private static string RenderRunners()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Predefined runners");
        builder.AppendLine();
        builder.AppendLine("A procedure specific to this repository - a corpus run, a benchmark, a round trip");
        builder.AppendLine("- is declared under predefinedRunners and started with 'DssHarness run <name>'.");
        builder.AppendLine("It runs across the legs it declares, with the same isolation, locking, stall");
        builder.AppendLine("bounds, witnesses and reporting build and test get.");
        builder.AppendLine();
        builder.AppendLine("A runner declares phases, or an action file, never both.");
        builder.AppendLine();
        builder.AppendLine($"  .harness-config/{HarnessLayout.RunnerDirectoryName}/{HarnessLayout.RunnerActionsDirectoryName}/<name>.yml");
        builder.AppendLine("                                     the steps, tracked by git");
        builder.AppendLine($"  .harness-config/{HarnessLayout.RunnerDirectoryName}/{HarnessLayout.RunnerEnvDirectoryName}/");
        builder.AppendLine("                                     values the steps read, ignored");
        builder.AppendLine($"  .harness-config/{HarnessLayout.RunnerDirectoryName}/{HarnessLayout.RunnerSecretsDirectoryName}/");
        builder.AppendLine("                                     secret values, ignored and never printed");
        builder.AppendLine();
        builder.AppendLine("A step either uses a predefined action or carries a run block. A run block is");
        builder.AppendLine("split on newlines and each line trimmed, so indentation and blank lines cannot");
        builder.AppendLine("change what runs. Each line is a program and its arguments, never a shell string.");
        builder.AppendLine("Quoting is double quotes only, no escapes, and a line with an unbalanced quote is");
        builder.AppendLine("refused when the file is read: the splitter silently drops everything after one,");
        builder.AppendLine("so the alternative is a command missing arguments nobody can see are missing.");
        builder.AppendLine("The first token must be a program declared under tools, or a path in the");
        builder.AppendLine("repository; anything else is refused before a single step runs.");
        builder.AppendLine();
        builder.AppendLine("expectedExceptions names failures a runner may produce, with the outcome to");
        builder.AppendLine("report instead. Every entry carries earnedOn, earnedAt, mechanism and anchor, and");
        builder.AppendLine("is refused without them: an excusal nobody can audit stops being a record of a");
        builder.AppendLine("measured confound and becomes a way to make a regression invisible. An entry");
        builder.AppendLine("naming no message, or one matching anything, is refused for the same reason.");
        builder.AppendLine();
        builder.AppendLine("runChecks gate an entry on another runner confirming the confound. Unconfirmed,");
        builder.AppendLine("the failure stays genuine. The window is the failing unit's own output up to its");
        builder.AppendLine("verdict line, never a once-per-run sample: a sample was measured charging");
        builder.AppendLine("genuine-looking failures to the tool on a loaded machine and excusing them on a");
        builder.AppendLine("quiet one, the same day.");

        return builder.ToString();
    }

    private static string RenderVerdicts()
    {
        var builder = new StringBuilder();

        builder.AppendLine("What each leg verdict means");
        builder.AppendLine();
        builder.AppendLine("Every declared leg reaches exactly one. A leg that reaches none is a defect in");
        builder.AppendLine("the harness and fails the run, rather than vanishing from the report.");
        builder.AppendLine();

        foreach (var info in Verdicts.All)
        {
            builder.AppendLine(
                $"  {info.Display,-22} {(info.IsFailure ? "counts as failure" : "not a failure"),-18} exit {info.ExitCode}");
        }

        builder.AppendLine();
        builder.AppendLine("When several apply the more fundamental one is reported, in the order above.");
        builder.AppendLine("A leg whose inputs moved is not reported as failed even when its tests failed,");
        builder.AppendLine("because what failed was a tree that never existed.");
        builder.AppendLine();
        builder.AppendLine("Remedies");
        builder.AppendLine($"  {LegExit.InputsMoved}  inputs-moved, unmeasured   Let the tree settle, then run again");
        builder.AppendLine($"  {LegExit.Contended}  contended                  Wait for the other run");
        builder.AppendLine($"  {LegExit.Unwitnessed}  unwitnessed                Find out what actually ran");
        builder.AppendLine($"  {LegExit.LogHeld}  log-held                   Find out which run still owns the logs");

        return builder.ToString();
    }

    private static string RenderOverview()
    {
        var builder = new StringBuilder();

        builder.AppendLine("DssHarness - one cross-platform tool for a repository's build, test and");
        builder.AppendLine("cross-host work. Every behaviour is declared in .harness-config/config.json;");
        builder.AppendLine("nothing about a specific repository or toolchain is built into the tool.");
        builder.AppendLine();
        builder.AppendLine("Getting started");
        builder.AppendLine("  DssHarness verify-git              Check git is present and this is a repository");
        builder.AppendLine("  DssHarness init                    Create .harness-config and seed config.json");
        builder.AppendLine("  DssHarness legs                    Show where each leg can run, or why it cannot");
        builder.AppendLine("  DssHarness install-missing-tools   Install what each leg's host is missing");
        builder.AppendLine("  DssHarness sync                    Put each host's copy in step with this tree");
        builder.AppendLine("  DssHarness build                   Build every selected leg");
        builder.AppendLine("  DssHarness test                    Build and test every selected leg");
        builder.AppendLine("  DssHarness run <runner>            Run a predefined runner across its legs");
        builder.AppendLine("  DssHarness list-worktree           Show existing worktrees");
        builder.AppendLine("  DssHarness read-anchors            List the deferred work recorded as anchors");
        builder.AppendLine();
        builder.AppendLine("Every command accepts");
        builder.AppendLine("  -C, --directory <dir>              Operate on this directory (default: current)");
        builder.AppendLine("  -v, --verbose                      Show per-phase detail and child process output");
        builder.AppendLine();
        builder.AppendLine("Topics");
        builder.AppendLine("  DssHarness help exit-codes         What each exit code means");
        builder.AppendLine("  DssHarness help config             What config.json declares");
        builder.AppendLine("  DssHarness help legs               Hosts, emulators, and how a leg finds where it runs");
        builder.AppendLine("  DssHarness help worktrees          Naming rules, the path budget, and when deleting refuses");
        builder.AppendLine("  DssHarness help anchors            Anchor registries and the commands that change them");
        builder.AppendLine("  DssHarness help layout             What init creates, and what git tracks");
        builder.AppendLine("  DssHarness help secrets            Where each host's connection data lives");
        builder.AppendLine("  DssHarness help tools              What install-missing-tools installs, and where");
        builder.AppendLine("  DssHarness help runners            Predefined runners, action files and excused failures");
        builder.AppendLine("  DssHarness help verdicts           What each leg verdict means, and what to do about it");
        builder.AppendLine();
        builder.AppendLine("Use 'DssHarness <command> --help' for a command's own options.");

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
        builder.AppendLine("  read-anchor, read-anchors --lint, check-anchor-balance, check-anchor-citations");
        builder.AppendLine($"    {AnchorExit.Findings,3}  an id was not found, the registries have problems, or the balance did not hold");
        builder.AppendLine();
        builder.AppendLine("  legs");
        builder.AppendLine($"    {LegsExit.Unavailable,3}  a leg named with --legs cannot run, or no selected leg can");
        builder.AppendLine();
        builder.AppendLine("  install-missing-tools");
        builder.AppendLine($"    {ToolsExit.NotProvisioned,3}  a tool is missing, out of date, or could not be installed");
        builder.AppendLine();
        builder.AppendLine("  check-ci-legs");
        builder.AppendLine($"    {CiExit.LegRed,3}  a leg is red");
        builder.AppendLine($"    {CiExit.MatrixDidNotRun,3}  the matrix did not run, which is never read as every leg passing");
        builder.AppendLine();
        builder.AppendLine("  build, test, run");
        builder.AppendLine($"    {LegExit.InputsMoved,3}  inputs-moved or unmeasured: let the tree settle, then run again");
        builder.AppendLine($"    {LegExit.Contended,3}  contended: wait for the other run");
        builder.AppendLine($"    {LegExit.Unwitnessed,3}  unwitnessed: find out what actually ran");
        builder.AppendLine($"    {LegExit.LogHeld,3}  log-held: find out which run still owns this leg's logs");
        builder.AppendLine();
        builder.AppendLine("host-exec returns the exit code of the command it ran on the host, unchanged, or");
        builder.AppendLine($"{HarnessExit.HostUnavailable} when nothing ran there, or the command never reported how it finished.");

        return builder.ToString();
    }

    private static string RenderLegs()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Legs and hosts");
        builder.AppendLine();
        builder.AppendLine("A leg says what it needs, never where it runs:");
        builder.AppendLine($"  os          {string.Join(", ", PlatformNames.OperatingSystems)}");
        builder.AppendLine($"  processor   {string.Join(", ", PlatformNames.Processors)}");
        builder.AppendLine("  emulator    optional: the emulator it runs through on a host with another");
        builder.AppendLine("              processor; a leg naming one never runs natively, and a leg naming");
        builder.AppendLine("              none runs only natively");
        builder.AppendLine();
        builder.AppendLine("Hosts are this machine, the WSL distributions under hosts.wsl and the ssh hosts");
        builder.AppendLine("under hosts.ssh. Before anything runs, hosts are measured, never assumed: their");
        builder.AppendLine("operating system, their processor, and whether each emulator the legs use works");
        builder.AppendLine("there. This machine is measured first, and other hosts only for the legs it cannot");
        builder.AppendLine("run. A leg runs on the first host that provides what it needs: this machine, then");
        builder.AppendLine("the WSL distributions, for a Linux leg only, then the ssh hosts, each in the order");
        builder.AppendLine("the configuration declares them. A leg that sets \"wsl\" or \"ssh\" runs on that");
        builder.AppendLine("host and nowhere else.");
        builder.AppendLine();
        builder.AppendLine("  DssHarness legs                          every declared leg");
        builder.AppendLine("  DssHarness legs --legs a,b gate          legs a and b, and the legs of set gate");
        builder.AppendLine("  DssHarness host-exec --ssh vps -- verify-git");
        builder.AppendLine("  DssHarness host-exec --wsl -- list-worktree");
        builder.AppendLine();
        builder.AppendLine("A leg no host can run is a warning that names it and says why; the other legs");
        builder.AppendLine("still go ahead. The check fails when a leg named with --legs cannot run, or when");
        builder.AppendLine("no selected leg can. --legs given without a name is refused, not taken for every leg.");
        builder.AppendLine();
        builder.AppendLine("A WSL distribution is available when this machine runs Windows, wsl.exe exists,");
        builder.AppendLine("and a program starts in the distribution; --wsl with no name is WSL's default.");
        builder.AppendLine("An ssh host is available when .harness-config/sshItems/<name>/ holds its connection");
        builder.AppendLine("data, named under sshItems and keyed the same way by hosts.ssh, ssh connects in batch");
        builder.AppendLine("mode, so without ever waiting at a prompt, and DssHarness runs there. An .env other");
        builder.AppendLine("users can change is refused, since whoever can change it can send the harness");
        builder.AppendLine("elsewhere, and a key they can read, ssh ignores; both are checked before connecting.");
        builder.AppendLine("Run 'DssHarness help secrets' for the layout.");
        builder.AppendLine();
        builder.AppendLine("Every WSL distribution and ssh host runs DssHarness itself, installed as a global");
        builder.AppendLine($".NET tool from nuget.org, so it needs the .NET {ToolPackage.MinimumSdkMajor} SDK. It must be this machine's build:");
        builder.AppendLine("a host that is behind is installed or updated to this version, never downgraded,");
        builder.AppendLine($"and a host that is ahead stops everything ({HarnessExit.Refused}) until this machine is updated. The");
        builder.AppendLine("version and a hash of the tool's own assembly are both compared, because a build");
        builder.AppendLine("from source reports the same version as the published package.");
        builder.AppendLine();
        builder.AppendLine("The DssHarness on a host is reached through a hidden host-agent command, with the");
        builder.AppendLine("request on standard input, held open while the host works: interrupting host-exec");
        builder.AppendLine("ends it, and the host cancels the command. The command line ssh hands a remote");
        builder.AppendLine("shell holds only fixed words, so no argument is ever reinterpreted by sh, cmd or");
        builder.AppendLine("PowerShell. host-exec runs in the host's copy of the repository at repositoryPath,");
        builder.AppendLine("which has to exist already: until sync exists, nothing creates it.");
        builder.AppendLine();
        builder.AppendLine("An emulator declares the hosts it runs on (hostOs, hostProcessor), the processor it");
        builder.AppendLine("runs programs for, the launcher placed in front of each program (such as");
        builder.AppendLine("qemu-aarch64 -L <sysroot>, or arch -x86_64; none for Prism and binfmt), what it");
        builder.AppendLine("requires, the phases it runs (test by default), and a witness: a program run");
        builder.AppendLine("through it whose output must match a pattern, proving it really runs programs for");
        builder.AppendLine("that processor. A launcher and a required file are each a program name, looked up");
        builder.AppendLine("on the host's PATH, or an absolute path. So is a witness with no launcher; behind a");
        builder.AppendLine("launcher a witness is an absolute path, since the launcher finds it, not the PATH.");
        builder.AppendLine();
        builder.AppendLine("legs and host-exec run what config.json declares: legs runs the witness of each");
        builder.AppendLine("emulator the selected legs use, and both run DssHarness itself on WSL distributions");
        builder.AppendLine("and ssh hosts, which they install or update there from nuget.org, and from nuget.org");
        builder.AppendLine("only. That is the trust building the repository already asks for. An ssh host is");
        builder.AppendLine("reached only when the main checkout holds its directory under sshItems, so a");
        builder.AppendLine("config.json arriving through git cannot point the harness at a machine nobody set up.");
        builder.AppendLine();
        builder.AppendLine("Exit codes");
        builder.AppendLine($"  {HarnessExit.Success,3}  legs: every named leg can run, or with no --legs, at least one leg can");
        builder.AppendLine($"  {LegsExit.Unavailable,3}  legs: a leg named with --legs cannot run, or no selected leg can");
        builder.AppendLine($"  {HarnessExit.UsageError,3}  --legs names something that is neither a leg nor a leg set, or no name at all");
        builder.AppendLine($"  {HarnessExit.Refused,3}  a host runs a newer {ToolPackage.Command} than this machine");
        builder.AppendLine($"  {HarnessExit.HostUnavailable,3}  host-exec: the host cannot run {ToolPackage.Command}, has no copy of the repository,");
        builder.AppendLine("       or the command never reported how it finished, so it may have run only in part");
        builder.AppendLine("       host-exec otherwise returns the exit code of the command it ran");

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
        builder.AppendLine();
        builder.AppendLine("delete-worktree removes a worktree, everything under it, and git's record of it.");
        builder.AppendLine($"Without --force it checks first, and deletes nothing and exits {HarnessExit.Refused} when the");
        builder.AppendLine("worktree has uncommitted changes: a modified, staged or untracked file git does");
        builder.AppendLine("not ignore, a changed submodule, or an edit hidden by assume-unchanged or");
        builder.AppendLine("skip-worktree. It refuses too when commits on its HEAD are on no branch, tag,");
        builder.AppendLine("remote-tracking ref, newest stash or other worktree's HEAD; when a submodule");
        builder.AppendLine("repository deleted with it, checked out or not, holds a commit no");
        builder.AppendLine("remote-tracking ref or tag contains, or a stash; when it is locked; when git's");
        builder.AppendLine("record of it names another directory or none, as after moving it by hand; and");
        builder.AppendLine("when git does not see it as a worktree of this repository. The refusal names");
        builder.AppendLine("everything it found, on one line. A tag made inside a submodule counts as kept,");
        builder.AppendLine("and is lost with the submodule's repository.");
        builder.AppendLine();
        builder.AppendLine("Without --force, when the check cannot be finished, because git cannot answer or");
        builder.AppendLine("a record cannot be read or its directory found, nothing is deleted and it");
        builder.AppendLine($"exits {HarnessExit.CommandFailed}.");
        builder.AppendLine("Ignored files are deleted unchecked, even ones no build makes again, such as");
        builder.AppendLine(".env, and so are ignored directories with everything in them, the history of a");
        builder.AppendLine("repository nested inside one included. --force skips every check and overrides");
        builder.AppendLine("a lock; whatever the worktree held is lost.");
        builder.AppendLine();
        builder.AppendLine("An interruption during the deletion can leave it partly done, on any platform;");
        builder.AppendLine("running delete-worktree again with --force finishes it.");

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
        builder.AppendLine($"  {HarnessExit.NotInitialized,3}  a registry is missing; run '{ToolPackage.Command} init'");
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
        builder.AppendLine("  .harness-config/runner/actions/    tracked; a predefined runner's steps, as YAML");
        builder.AppendLine("  .harness-config/runner/.env/       contents ignored; values actions read");
        builder.AppendLine("  .harness-config/runner/.secrets/   contents ignored; secret values actions read");
        builder.AppendLine("  .harness-config/sshItems/<name>/   ignored, and written by you, not init:");
        builder.AppendLine("                                     .env (address, user, port), .key, known_hosts");
        builder.AppendLine("  .harness-config/wslDistros/<name>/ ignored; .env (distribution, credential)");
        builder.AppendLine("  .harness-config/worktrees/         contents ignored, .gitkeep tracked");
        builder.AppendLine("                                     (the default; worktrees.root moves it)");
        builder.AppendLine("  .harness-config/runs/              ignored; one directory of logs per run");
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
        builder.AppendLine("part of .harness-config through git but never the ignored part, so connection");
        builder.AppendLine("data and the run lock resolve back to the originating checkout. Action files");
        builder.AppendLine("are tracked, so a worktree has its own and a runner acts on the tree it was");
        builder.AppendLine("asked about.");

        return builder.ToString();
    }

    private static string RenderConfig()
    {
        var builder = new StringBuilder();

        builder.AppendLine("config.json");
        builder.AppendLine();
        builder.AppendLine($"  defaults       buildCores and testCores ({HarnessDefaults.DefaultCores} each), maxParallelLegs, default");
        builder.AppendLine("                 project, stall bound");
        builder.AppendLine("  toolchains     compilers, as environment and cache variables (msvc, gcc, clang)");
        builder.AppendLine("  sanitizers     instrumentation overlays composed onto a build");
        builder.AppendLine("  buildConfigs   named configurations (debug, release, o1, o2)");
        builder.AppendLine("  projects       what to build, and with which adapter (cmake, dotnet, dart)");
        builder.AppendLine("  hosts          this machine (local), WSL distributions (wsl) and ssh hosts (ssh),");
        builder.AppendLine("                 each with its own core counts when they differ");
        builder.AppendLine("  emulators      ways to run programs for another processor on a host: qemu,");
        builder.AppendLine("                 Rosetta, Prism");
        builder.AppendLine("  legs           units of work: os + processor (+ emulator) + project + toolchain");
        builder.AppendLine("                 + config (+ sanitizer)");
        builder.AppendLine("  legSets        named groups of legs, selected with --legs like a leg");
        builder.AppendLine("  tools          external tools to verify and install");
        builder.AppendLine("  runners        multi-phase procedures such as a corpus test or a benchmark");
        builder.AppendLine("  exec           named commands to run through 'DssHarness exec'");
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
        builder.AppendLine("a leg the order is fixed: sync when the host needs it, then build on buildCores");
        builder.AppendLine("cores, then test on testCores cores. Where a leg runs is measured before anything");
        builder.AppendLine("starts; see 'DssHarness help legs'.");
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
