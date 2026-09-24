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
using RepoHarness.Core.Runners;
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
            + $"secrets, tools, runners, verdicts, ci.{Environment.NewLine}",
    };

    private static string RenderTools()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Installing what a host is missing");
        builder.AppendLine();
        builder.AppendLine("'install-missing-tools' runs over every declared leg, or those --legs names, on the");
        builder.AppendLine("host each leg names with wsl or ssh, and on this machine otherwise. 'init");
        builder.AppendLine("--install-tools' runs it too; plain init installs nothing and says how.");
        builder.AppendLine();
        builder.AppendLine("--dry-run reaches and asks every host as a run does, and installs nothing: each tool");
        builder.AppendLine("it would install or update says 'would install' or 'would update' with the command");
        builder.AppendLine("that would run, sudo and all, and nobody is asked for a password or whether one is");
        builder.AppendLine("needed. It exits 1 while anything is missing, as a run that could not install it does.");
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
        builder.AppendLine("An entry may name the platforms it is needed on:");
        builder.AppendLine();
        builder.AppendLine("  { \"name\": \"cl\", \"platforms\": [\"windows\"] }");
        builder.AppendLine();
        builder.AppendLine("A host whose platform an entry does not name is never asked about it, so it is");
        builder.AppendLine("neither probed there nor counted against that host's legs. Left out, a tool is");
        builder.AppendLine("needed everywhere. Without this a repository could not declare both a Windows");
        builder.AppendLine("compiler and a POSIX one: each was reported missing on the other's hosts.");
        builder.AppendLine();
        builder.AppendLine("It may narrow that to the legs that need it; every scope it names must hold:");
        builder.AppendLine();
        builder.AppendLine("  \"toolchains\": [\"msvc\"]        legs whose variant builds with one of these");
        builder.AppendLine("  \"legs\": [\"win-arm\", \"gate\"]   these legs, or the legs of these leg sets");
        builder.AppendLine("  \"processors\": [\"arm64\"]       legs built for one of these: the leg's processor,");
        builder.AppendLine("                                which under an emulator is not the host's");
        builder.AppendLine("  \"emulators\": [\"qemu-arm64\"]   legs run under one of these emulators - how to");
        builder.AppendLine("                                scope what the emulating host needs, such as the");
        builder.AppendLine("                                emulator itself, which a native arm64 host does not");
        builder.AppendLine();
        builder.AppendLine("Two legs on one host share what is installed there, and each is told only about");
        builder.AppendLine("the tools it needs: cl scoped to msvc is never missing on a MinGW leg of the same");
        builder.AppendLine("machine. Each is told about a tool as it will find it: a leg whose toolchain names");
        builder.AppendLine("a developer environment, on the PATH that environment sets up for its processor -");
        builder.AppendLine("set up on this machine for the look, so cl is found where Visual Studio keeps it -");
        builder.AppendLine("and any other leg on its host's own PATH. An install runs once on a host, whichever");
        builder.AppendLine("leg asked first, and each is then told what it finds. On another host, a tool its");
        builder.AppendLine("own PATH lacks is unknown for a leg in a developer environment, which this command");
        builder.AppendLine("sets up only here, and nothing is installed for it. A tool no selected leg on a");
        builder.AppendLine("host needs is not looked for there at all. A scope naming nothing declared, or one");
        builder.AppendLine("covering no declared leg, is refused.");
        builder.AppendLine();
        builder.AppendLine("A privileged install takes its credential from that host's own item, on standard");
        builder.AppendLine("input only: it reaches no argument list, no log and no error message. A second run");
        builder.AppendLine("reports 'already current' and changes nothing. An unreachable host is named, and");
        builder.AppendLine("the other legs still go ahead.");
        builder.AppendLine();
        builder.AppendLine("Where a program is found");
        builder.AppendLine();
        builder.AppendLine("A command run over ssh or with wsl.exe reads no login profile, so its PATH is not the");
        builder.AppendLine("one a terminal shows: on macOS it lacks /opt/homebrew/bin. A program is looked for on");
        builder.AppendLine("that PATH first, then in the searched directories - on macOS and Linux:");
        builder.AppendLine();
        builder.AppendLine("  " + string.Join(" ", ToolSearchDirectories.Posix));
        builder.AppendLine();
        builder.AppendLine("and on Windows none, whose installers put a program on the machine PATH. A login");
        builder.AppendLine("shell is not asked instead: a profile that replaces PATH was measured making");
        builder.AppendLine("'command -v' answer wrongly over ssh.");
        builder.AppendLine();
        builder.AppendLine("  \"toolSearchDirectories\": { \"macos\": [\"/opt/local/bin\", \"~/bin\"] }");
        builder.AppendLine();
        builder.AppendLine("replaces the searched directories for a platform, or for 'all', when its list names");
        builder.AppendLine("something that platform can search; declared empty, or naming only another");
        builder.AppendLine("platform's directories, the built-in list stays. Each entry is '~/' for the");
        builder.AppendLine("home directory of whoever searches, or absolute for its platform: a drive or a share");
        builder.AppendLine("on Windows, a leading '/' elsewhere. Under 'all', an entry only one kind of machine");
        builder.AppendLine("can name is searched where it can be. The harness's own .NET SDK is always looked for");
        builder.AppendLine("in the built-in list, so a repository's list cannot hide it.");
        builder.AppendLine();
        builder.AppendLine("The search runs on the machine that runs the leg, and 'legs' and the leg's own run");
        builder.AppendLine("ask it through one function there. The directory each program was found in, on the");
        builder.AppendLine("PATH or off it, is appended to the PATH of every process the leg starts - even one");
        builder.AppendLine("whose environment sets PATH itself - so the run finds what the survey found, and a");
        builder.AppendLine("build tool's own children, ninja and the compilers cmake starts, are found where it");
        builder.AppendLine("was. Appended, never prepended: a name the PATH already answers for keeps that");
        builder.AppendLine("answer. install-missing-tools, which runs before the harness is on a host, looks in");
        builder.AppendLine("the same directories through the host's shell; where that shell cannot look, it");
        builder.AppendLine("says the tool is unknown rather than missing, and installs nothing.");
        builder.AppendLine();
        builder.AppendLine("A leg goes to the first of its hosts that can take it - the right machine, reached -");
        builder.AppendLine("whatever the command, which is where a sync puts its tree and where --use-staged");
        builder.AppendLine("finds it. It is turned away there when that host lacks a program its command will");
        builder.AppendLine("start:");
        builder.AppendLine();
        builder.AppendLine("  build   the project's build program, ninja for a Ninja generator, and the");
        builder.AppendLine("          compilers CC and CXX name");
        builder.AppendLine("  test    those, and the test runner; with --no-build, the runner alone");
        builder.AppendLine("  run     what the runner's steps start, and the build's too with requireBuild");
        builder.AppendLine("  sync    nothing: a copy starts no program");
        builder.AppendLine("  legs    what build and test start");
        builder.AppendLine();
        builder.AppendLine("A missing program never moves a leg to another host: that would measure a machine");
        builder.AppendLine("nobody chose. To run a leg elsewhere, name the host in the leg ('ssh' or 'wsl').");
        builder.AppendLine();
        builder.AppendLine("Not every declared tool - a tool one runner needs does not make every other leg on");
        builder.AppendLine("a host without it unrunnable. A program is looked for when it is named by name, or");
        builder.AppendLine("by a path absolute on the leg's platform. CC and CXX are read as CMake reads them");
        builder.AppendLine("where the value alone can say: the whole value when it holds no space, else its");
        builder.AppendLine("first word when that is a name; any other value is left to CMake. The rest is the");
        builder.AppendLine("run's to find: a relative path, read from the directory its phase starts in; and a");
        builder.AppendLine("name with a placeholder in it. A program started under an environment the");
        builder.AppendLine("configuration declares that sets PATH - the build's, the test invocation's, the");
        builder.AppendLine("runner's, a phase's or a step's - is looked for too, so its directory reaches that");
        builder.AppendLine("PATH, and never turns a leg away: that PATH is where it is found when it starts. A");
        builder.AppendLine("PATH among a runner's values is not seen before the run; name its directory under");
        builder.AppendLine("toolSearchDirectories instead.");
        builder.AppendLine();
        builder.AppendLine("A leg turned away for a missing program is 'skipped-tool-missing', and the run is");
        builder.AppendLine($"incomplete, exit {HarnessExit.Incomplete} - or exit {LegsExit.Unavailable} when no selected leg can run at all. A leg");
        builder.AppendLine("that is already running when a program will not start has 'failed', naming the");
        builder.AppendLine("program and the reason the system gave: no survey could have required it - a file");
        builder.AppendLine("the build was to make, a binary for another processor. Neither is 'poisoned', which");
        builder.AppendLine("is kept for a defect in this tool.");

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
        builder.AppendLine("Each action owns one directory, and its file carries that directory's name, so a");
        builder.AppendLine("runner's 'action' is '<name>/<name>.yml'. Directories above that one group actions");
        builder.AppendLine("and are yours to arrange: 'real-examples/c/probe-nest/probe-nest.yml' is the action");
        builder.AppendLine("'probe-nest', grouped under 'real-examples/c'. What they may not be is actions");
        builder.AppendLine("themselves - a directory holding a file of its own name is an action, and an action");
        builder.AppendLine("cannot contain another, because everything beside an action's file belongs to it.");
        builder.AppendLine("A run line is a program and its arguments");
        builder.AppendLine("with no shell, so anything that is not a one-liner lives in a file; the directory is");
        builder.AppendLine("where that file goes, owned by the action it belongs to and read by the same review.");
        builder.AppendLine("A flat '<name>.yml', a path leaving the directory, and a file whose name differs from");
        builder.AppendLine("its directory's are each refused, naming the path the runner should have had.");
        builder.AppendLine();
        builder.AppendLine($"No directory along that path may be called '{HarnessLayout.ActionBuildDirectoryName}' or "
            + $"'{HarnessLayout.ActionArtifactsDirectoryName}', at any depth.");
        builder.AppendLine("Every action already owns one of each, below, and a directory that was both would be");
        builder.AppendLine("ignored by the rules that keep a run's output out of git - so the action's own file");
        builder.AppendLine("would never be committed, and what a reviewer reads would not be what runs.");
        builder.AppendLine();
        builder.AppendLine($"  {HarnessLayout.RunnerActionsDirectoryRelative}/<name>/<name>.yml");
        builder.AppendLine("                                     the steps, tracked by git");
        builder.AppendLine($"  {HarnessLayout.RunnerActionsDirectoryRelative}/<name>/...");
        builder.AppendLine("                                     whatever those steps run, beside them");
        builder.AppendLine($"  {HarnessLayout.RunnerDirectoryRelative}/{HarnessLayout.RunnerEnvDirectoryName}/");
        builder.AppendLine("                                     values the steps read, ignored");
        builder.AppendLine($"  {HarnessLayout.RunnerDirectoryRelative}/{HarnessLayout.RunnerSecretsDirectoryName}/");
        builder.AppendLine("                                     secret values, ignored and never printed");
        builder.AppendLine();
        builder.AppendLine("A sync carries the actions to every host the way it carries the rest of the tree:");
        builder.AppendLine("what git does not ignore crosses, committed or not, and an action the tree no");
        builder.AppendLine("longer has is removed from a host that has it. Nothing else in .harness-config");
        builder.AppendLine("crosses except config.json - the one this command read, which for a worktree is");
        builder.AppendLine("that worktree's. Connection data, secrets, values, locks and runs stay on their");
        builder.AppendLine("machine, and so does each action's own build and artifacts, whatever .gitignore says.");
        builder.AppendLine();
        builder.AppendLine("A run that uses an action asks git first whether that action's build and artifacts");
        builder.AppendLine("are ignored, and refuses before any step when either is not, naming the missing");
        builder.AppendLine("rule and 'init' as the fix: a run's output written where git would commit it is a");
        builder.AppendLine("measurement nobody can reproduce. Where git cannot answer in a leg's tree - a copy it");
        builder.AppendLine("will not trust, a worktree whose link is broken - that leg is unavailable, in git's");
        builder.AppendLine("own words, and the other legs still run.");
        builder.AppendLine();
        builder.AppendLine("A step either uses a predefined action or carries a run block. A run block is");
        builder.AppendLine("split on newlines and each line trimmed, so indentation and blank lines cannot");
        builder.AppendLine("change what runs. Each line is a program and its arguments, never a shell string.");
        builder.AppendLine("Quoting is double quotes only, no escapes, and a line with an unbalanced quote is");
        builder.AppendLine("refused when the file is read: the splitter silently drops everything after one,");
        builder.AppendLine("so the alternative is a command missing arguments nobody can see are missing.");
        builder.AppendLine("The first token must be a program declared under tools, or a path in the");
        builder.AppendLine("repository; anything else is refused before a single step runs. It is judged as");
        builder.AppendLine("it will start, its names filled in and read from the directory its step runs in:");
        builder.AppendLine("'{dir}/tool' is inside the repository only where {dir} keeps it there. A line, or");
        builder.AppendLine("a step's directory, that fills in to nothing is refused the same way, naming it.");
        builder.AppendLine();
        builder.AppendLine("A step runs at the leg's tree root unless it says otherwise, which is what a step");
        builder.AppendLine("with neither key below has always done. The new layout reads as though a step ran");
        builder.AppendLine("beside its own file; it does not, unless it asks to:");
        builder.AppendLine();
        builder.AppendLine($"  workingDirectoryRoot: {string.Join(" | ", WorkingDirectoryRoots.All)}   what the path below starts from");
        builder.AppendLine("  workingDirectory: <path>            under that root; absent, the root itself");
        builder.AppendLine();
        builder.AppendLine($"  {WorkingDirectoryRoots.Tree,-8} the leg's worktree, or the repository. The default.");
        builder.AppendLine($"  {WorkingDirectoryRoots.Harness,-8} the tree's .harness-config directory.");
        builder.AppendLine($"  {WorkingDirectoryRoots.Action,-8} the action's own directory, beside the files it ships.");
        builder.AppendLine();
        builder.AppendLine($"So a step running a program it ships says 'workingDirectoryRoot: {WorkingDirectoryRoots.Action}' and");
        builder.AppendLine("then names it as './probe.py': a program named by a relative path is read from the");
        builder.AppendLine("directory its step runs in, by the run and by the check that allows it alike. A");
        builder.AppendLine("step that builds or tests the repository says nothing and keeps the tree root, as");
        builder.AppendLine("every step written before this did.");
        builder.AppendLine();
        builder.AppendLine($"  runOn: [{string.Join(", ", PlatformNames.OperatingSystems)}]   the operating systems the step runs on");
        builder.AppendLine();
        builder.AppendLine("A step without runOn runs on every leg. A leg of another operating system skips a");
        builder.AppendLine("step that names runOn, says so as it runs, and lists it as skippedSteps on its line");
        builder.AppendLine("in --json; nothing about the step is asked of that leg - not its program, which no");
        builder.AppendLine("host is turned away for, and not the names its lines use. A step that reads what a");
        builder.AppendLine("skipped step would have made finds nothing there, so give both the same runOn. A");
        builder.AppendLine("run in which some leg would run no step at all is refused before anything starts,");
        builder.AppendLine("naming the leg: it would pass having run nothing.");
        builder.AppendLine();
        builder.AppendLine("  successPattern: <regular expression>   what the step's last line must print");
        builder.AppendLine();
        builder.AppendLine("A step passes when each of its lines exits 0; one that declares successPattern must");
        builder.AppendLine("also have its last line print something the pattern matches, since a program that");
        builder.AppendLine("exits 0 has not shown it did anything. The pattern is a .NET regular expression,");
        builder.AppendLine("matched with ^ and $ at each line, against that line's standard output and standard");
        builder.AppendLine("error read together, after secrets are redacted. One that does not compile, or that");
        builder.AppendLine("is empty and so matches anything, is refused when the file is read. The earlier lines");
        builder.AppendLine("of a run block answer with their exit codes alone: the witness belongs to the step,");
        builder.AppendLine("and its last line finishing is its work being done.");
        builder.AppendLine();
        builder.AppendLine("Names a run line may use");
        builder.AppendLine();
        builder.AppendLine("A run line is written for this tool, so a name in braces it cannot fill in is");
        builder.AppendLine("refused over the whole file before the first program starts. '${NAME}' belongs to");
        builder.AppendLine("another expander and is never touched; a brace meant literally is doubled, so");
        builder.AppendLine("awk '{{print}}' reaches awk as '{print}'.");
        builder.AppendLine();
        builder.AppendLine("  {treeDir} {buildDir} {harnessDir}   the leg's tree, its variant-keyed build");
        builder.AppendLine("                                      directory, and the tree's .harness-config");
        builder.AppendLine("  {leg} {os} {processor}              which leg this is, derived rather than typed:");
        builder.AppendLine("  {toolchain} {config} {variant}      an instrument that records what it measured");
        builder.AppendLine("  {host} {runId}                      cannot then be labelled wrongly by hand");
        builder.AppendLine("  {product}                           the one file this build is declared to make.");
        builder.AppendLine("                                      Refused where the project declares none or");
        builder.AppendLine("                                      several, rather than guessing which");
        builder.AppendLine("  <input name>                        any input the action declares, by its name");
        builder.AppendLine();
        builder.AppendLine($"An action's 'inputs' are resolved from 'run {CommandLineInputs.Option} <name>=<value>' first, the");
        builder.AppendLine("runner value directories second and each input's own 'default' last. A required");
        builder.AppendLine("input with none of them is refused before the first step runs. The same values");
        builder.AppendLine("reach the steps as INPUT_<NAME> in the environment, which is how a secret is handed");
        builder.AppendLine("over: a value spliced into a command line reaches the process table, where anything");
        builder.AppendLine("on the machine can read it.");
        builder.AppendLine();
        builder.AppendLine($"{CommandLineInputs.Option} takes one name=value each time it is given, for an input the action");
        builder.AppendLine("declares, and only for the runner the command line names: a runner a run check");
        builder.AppendLine("starts reads its own values. A host running one of the run's legs is given the same");
        builder.AppendLine("values. Any other name, a runner of phases, an empty value and a name given twice");
        builder.AppendLine("are refused before a host is measured - an unset shell variable is not a request to");
        builder.AppendLine("run with nothing. The value is a plain one, on a command line; a secret stays in");
        builder.AppendLine($"{HarnessLayout.RunnerSecretsDirectoryName}.");
        builder.AppendLine();
        builder.AppendLine("What a step produces");
        builder.AppendLine();
        builder.AppendLine("A step may say what it makes, and what it makes may outlive the run:");
        builder.AppendLine();
        builder.AppendLine("  outputs: [<path>, ...]   files the step writes, relative to its own directory");
        builder.AppendLine("  persist: true            keep them when the action finishes");
        builder.AppendLine();
        builder.AppendLine("  {stepBuild}         this step's own directory, created before it runs");
        builder.AppendLine("  {actionBuild}       this leg's directory for this run, holding one per step");
        builder.AppendLine("  {actionArtifacts}   where this leg's kept outputs go");
        builder.AppendLine("  {runArtifacts}      this run's kept outputs across every leg, one");
        builder.AppendLine("                      directory per leg. How a step reads what another");
        builder.AppendLine("                      leg produced");
        builder.AppendLine();
        builder.AppendLine("  <action>/build/<run id>/<step>/       while the run lasts");
        builder.AppendLine("  <action>/artifacts/<run id>/<step>/   what it asked to keep");
        builder.AppendLine();
        builder.AppendLine("Both are gitignored, and neither is written into directly: keyed by the run, two");
        builder.AppendLine("legs or a retry cannot write over each other. A declared output that is not there");
        builder.AppendLine("when the step exits zero makes the leg 'unwitnessed' - exiting zero having written");
        builder.AppendLine("nothing is the failure an exit code alone can never report. The build directory is");
        builder.AppendLine("emptied when the action finishes, whatever the verdict, so anything a later run");
        builder.AppendLine("needs has to say 'persist'.");
        builder.AppendLine();
        builder.AppendLine("Outputs are kept as soon as the step that made them passes, not at the end of the");
        builder.AppendLine("run, because a later step reads them. A step that FAILED keeps nothing, although it");
        builder.AppendLine("may have written the file: carrying evidence out of work that did not pass is what");
        builder.AppendLine("the whole verdict vocabulary exists to refuse.");
        builder.AppendLine();
        builder.AppendLine("This is also what survives a resumed run. A step already done is skipped, and the");
        builder.AppendLine("build directory of the attempt that ran it is gone - so a later step reaching an");
        builder.AppendLine("earlier one's work across a resume has to read it from the artifacts, not from");
        builder.AppendLine("'{actionBuild}'.");
        builder.AppendLine();
        builder.AppendLine("Carrying an artifact to another machine");
        builder.AppendLine();
        builder.AppendLine("  DssHarness sync --artifact <run id>");
        builder.AppendLine();
        builder.AppendLine("carries exactly that run's kept artifacts to each host, at the same relative path");
        builder.AppendLine("they have here - the run and the producing leg are already in it, so a consuming");
        builder.AppendLine("step's '{runArtifacts}/<leg>/<step>/<file>' works unchanged on the far side. Each");
        builder.AppendLine("file is verified after it lands, for the reason --pull verifies: an artefact carried");
        builder.AppendLine("machine to machine is evidence that something built there runs here, and evidence");
        builder.AppendLine("nobody checked is not evidence.");
        builder.AppendLine();
        builder.AppendLine("A run that kept nothing is refused by name, whether or not any host needed a copy.");
        builder.AppendLine("An ordinary sync never carries these directories, whatever .gitignore says; this");
        builder.AppendLine("carries one run's, by name, and nothing else.");
        builder.AppendLine();
        builder.AppendLine("It writes into a copy that is already there and makes none of its own: a host with");
        builder.AppendLine("no copy, or one the harness did not create, is refused before a single file leaves");
        builder.AppendLine("this machine. Run 'DssHarness sync' first - with '--adopt \"<host>\"' where a");
        builder.AppendLine("directory is already at that repositoryPath. Otherwise a mistyped repositoryPath");
        builder.AppendLine("would be filled in rather than noticed.");
        builder.AppendLine();
        builder.AppendLine("A carry lands whole or not at all. Interrupted, it takes back what it had already");
        builder.AppendLine("written and says so - a directory holding two thirds of what the producer kept is");
        builder.AppendLine("the same path holding fewer files, and a step reading it would measure less than was");
        builder.AppendLine("built and pass. With '--dry-run' it carries nothing and lists what it would carry,");
        builder.AppendLine("as '--pull' does.");

        builder.AppendLine();
        builder.AppendLine("A step may ask for the guards the build and test verbs carry. Both are off unless");
        builder.AppendLine("asked for, and both watch the whole action rather than one step: an action of five");
        builder.AppendLine("steps has four gaps between them, and a file changed in a gap is the same moving");
        builder.AppendLine("tree as one changed inside a step.");
        builder.AppendLine();
        builder.AppendLine("  watchContention: true       sample the process table against the leg's build");
        builder.AppendLine("                              directory, so another run building there is reported");
        builder.AppendLine("                              rather than silently shared with. Needs a leg: an");
        builder.AppendLine("                              action run without one is refused, because a sample of");
        builder.AppendLine("                              no directory reports a clean one.");
        builder.AppendLine("  requireInputsUnmoved: true  fingerprint the tracked files before, during and after,");
        builder.AppendLine("                              so a tree edited while the step ran is reported rather");
        builder.AppendLine("                              than producing a result describing no tree that existed.");
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
        builder.AppendLine($"A run where nothing failed but some leg reached no verdict exits {HarnessExit.Incomplete}, not 0, and");
        builder.AppendLine("names the legs that did not report. A leg that never ran proves nothing about the");
        builder.AppendLine("code, so counting it among the legs that passed reports evidence nobody gathered.");
        builder.AppendLine();
        builder.AppendLine("When several apply the more fundamental one is reported, in the order above.");
        builder.AppendLine("A leg whose inputs moved is not reported as failed even when its tests failed,");
        builder.AppendLine("because what failed was a tree that never existed.");
        builder.AppendLine();
        builder.AppendLine("A run's closing line names its verdict with the count of legs that reached it, and");
        builder.AppendLine("then the rest, worst first - 'poisoned: 2 of 8 leg(s); 3 failed, 3 passed' - never");
        builder.AppendLine("the verdict beside the total, which reads as every leg having it.");
        builder.AppendLine();
        builder.AppendLine("A tool contention.sharedResourceTools lists, found running beside a leg, is a");
        builder.AppendLine("warning: one line per tool and per whose it was, with a count and the range of");
        builder.AppendLine("process ids. A process working in another declared leg's build directory is named");
        builder.AppendLine("as that leg's - two legs on one host share its load, which is maxParallelLegs at");
        builder.AppendLine("work, and yours to decide about. contention.sharedState says what each tool shares,");
        builder.AppendLine("in the words the warning uses:");
        builder.AppendLine();
        builder.AppendLine("  \"sharedState\": { \"ccache\": \"the per-user compiler cache\" }");
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
        builder.AppendLine($"    {HarnessExit.InternalError,3}  whether a leg can run was never established, through a defect in this tool");
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
        builder.AppendLine("A toolchain declares the platforms it exists on, and a leg naming one absent from");
        builder.AppendLine("its own operating system is refused when config.json is read - not skipped later as");
        builder.AppendLine("a missing compiler. Nothing about that needs measuring: a leg says which system it");
        builder.AppendLine("needs, and it only ever runs on a host that provides it.");
        builder.AppendLine();
        builder.AppendLine("A toolchain CMake builds with also names its compiler - CC or CXX under env,");
        builder.AppendLine("CMAKE_C_COMPILER, CMAKE_CXX_COMPILER or CMAKE_TOOLCHAIN_FILE under cacheVars, or the");
        builder.AppendLine("compilerId below - and one naming none is refused the same way: CMake would take");
        builder.AppendLine("whatever compiler it found first. One only .NET or Dart builds with names none. A");
        builder.AppendLine("build directory is then held to the compiler it was configured with by the file that");
        builder.AppendLine("compiler starts on the build's own PATH, and by its name only where that PATH holds");
        builder.AppendLine("no such program, so a second gcc earlier on the PATH is refused rather than mixed");
        builder.AppendLine("into the objects of the first.");
        builder.AppendLine();
        builder.AppendLine("Every CMake configure is asked which compilers it resolved - its file API's");
        builder.AppendLine("toolchains-v1 answer, which CMake 3.20 and later write - and each leg's line names");
        builder.AppendLine("them, 'compiler: MSVC 19.51.36231 (C, CXX)', whatever its verdict, and as compilers in");
        builder.AppendLine("--json. A toolchain may hold them to what it means:");
        builder.AppendLine();
        builder.AppendLine("  \"compilerId\": { \"C\": \"MSVC\", \"CXX\": \"MSVC\" }   CMake's own ids, by language");
        builder.AppendLine();
        builder.AppendLine("A build CMake configured with another compiler fails before anything is built with");
        builder.AppendLine("it; one CMake identified no compiler for is unwitnessed, saying why. A language only a");
        builder.AppendLine("subproject enables - C, where a C++ project fetches googletest - comes with no id in");
        builder.AppendLine("CMake's answer, and is identified from CMake's own record of it, the one enabling it");
        builder.AppendLine("loaded: CMakeFiles/<version>/CMake<language>Compiler.cmake - where the record names");
        builder.AppendLine("the compiler the answer names and is no newer than the answer, since a configure that");
        builder.AppendLine("identified the compiler again and then failed leaves a record of one nothing built with.");
        builder.AppendLine("test --no-build holds the directory it tests to it the same way. A configure that");
        builder.AppendLine("fails names no compiler, never the one an earlier configure resolved.");
        builder.AppendLine();
        builder.AppendLine("A toolchain may name the developer environment its legs start in, declared once");
        builder.AppendLine("under developerEnvironments:");
        builder.AppendLine();
        builder.AppendLine("  \"developerEnvironments\": { \"visualStudio\": { \"kind\": \"visualStudio\" } }");
        builder.AppendLine("  \"toolchains\": { \"msvc\": { ..., \"developerEnvironment\": \"visualStudio\" } }");
        builder.AppendLine();
        builder.AppendLine("Every host a leg might land on is asked whether it can set it up - for Visual");
        builder.AppendLine("Studio, whether its installer's vswhere names an instance with requiresComponent,");
        builder.AppendLine("the C++ build tools by default - and a host without one turns the leg away as a");
        builder.AppendLine("tool missing, saying why; one that could not look, as unavailable. On the host that");
        builder.AppendLine("runs the leg, the instance its survey found has its vcvarsall.bat run for the leg's");
        builder.AppendLine("processor, cross-compiling where the host's differs, and what it set - cl, link and");
        builder.AppendLine("the Windows SDK on PATH, INCLUDE, LIB - is what every process of the leg starts");
        builder.AppendLine("with, so cl builds from a plain shell. One that fails, reports an [ERROR, or sets up");
        builder.AppendLine("another processor fails the leg before anything of it starts. The programs the leg");
        builder.AppendLine("starts are then looked for on the PATH it set up, and one missing there skips the");
        builder.AppendLine("leg as a tool missing, named, before anything of it starts. Each leg's line names it:");
        builder.AppendLine("'developer environment: visualStudio (Visual Studio 18.0.11205.157, MSVC 14.50.35717,");
        builder.AppendLine("amd64)', and developerEnvironment in --json. A copy starts nothing there, and needs none.");
        builder.AppendLine();
        builder.AppendLine("Selected legs run at once and the command waits for all of them, reporting each");
        builder.AppendLine("live. Legs are chunked by the PHYSICAL machine they run on: this machine and every");
        builder.AppendLine("WSL distribution are one machine, because a distribution runs on it, and each ssh");
        builder.AppendLine("host is its own. defaults.maxParallelLegs caps how many run at once on any one");
        builder.AppendLine("machine, so a busy laptop is not asked for more than it has while the remote hosts");
        builder.AppendLine("sit idle; defaults.maxParallelLegsTotal caps the whole fleet on top of that, for");
        builder.AppendLine("what it shares even when its machines do not - a license server, a network share.");
        builder.AppendLine("Every line says which leg it came from, and a child's own output under --verbose");
        builder.AppendLine("is tagged '<leg>/<phase>:', so several hosts building at once stay readable.");
        builder.AppendLine();
        builder.AppendLine("Hosts are this machine, the WSL distributions under hosts.wsl and the ssh hosts");
        builder.AppendLine("under hosts.ssh. Before anything runs, hosts are measured, never assumed: their");
        builder.AppendLine("operating system, their processor, and whether each emulator the legs use works");
        builder.AppendLine("there. This machine is measured first, and other hosts only for the legs it cannot");
        builder.AppendLine("take. A leg runs on the first host that can take it - its operating system, its");
        builder.AppendLine("processor, and its emulator where it names one: this machine, then the WSL");
        builder.AppendLine("distributions, for a Linux leg only, then the ssh hosts, each in the order the");
        builder.AppendLine("configuration declares them - and is turned away there when that host lacks a");
        builder.AppendLine("program its command starts. A leg that sets \"wsl\" or \"ssh\" runs on that host and");
        builder.AppendLine("nowhere else.");
        builder.AppendLine();
        builder.AppendLine("  DssHarness legs                          every declared leg");
        builder.AppendLine("  DssHarness legs --legs a,b gate          legs a and b, and the legs of set gate");
        builder.AppendLine("  DssHarness host-exec --ssh vps -- verify-git");
        builder.AppendLine("  DssHarness host-exec --wsl -- list-worktree");
        builder.AppendLine();
        builder.AppendLine("Every cmake build also has its dependency records read, with 'ninja -t deps'.");
        builder.AppendLine("An object that recorded no header dependencies is never rebuilt when a header it");
        builder.AppendLine("includes changes, so the next build links yesterday's object and reports success.");
        builder.AppendLine("Only an object built under deps = msvc can record none legitimately: cl reports");
        builder.AppendLine("headers, never the source, and ninja drops the ones under 'program files' or");
        builder.AppendLine("'microsoft visual studio' as the system's, and a unit built from a precompiled");
        builder.AppendLine("header under /Yu never reports a header the precompiled header holds and guards.");
        builder.AppendLine("Such a zero is excused only where ninja rebuilds the object all the same for every");
        builder.AppendLine("header it surely includes - what its command force-includes with /FI, and what its");
        builder.AppendLine("source and those headers include by a quoted include beside them that ninja keeps,");
        builder.AppendLine("outside every comment, in no #if block its language may not compile: through its");
        builder.AppendLine("build line's inputs, and what built them recorded, as a unit built from a");
        builder.AppendLine("precompiled header is through the object compiling it. Another object's record");
        builder.AppendLine("never excuses it: an older build, or a compiler cache, may have written that one.");
        builder.AppendLine("How each object is built is read as ninja reads it, across the files build.ninja");
        builder.AppendLine("includes - CMake keeps its rules in CMakeFiles/rules.ninja - with its escapes");
        builder.AppendLine("undone and its variables evaluated, so its command is the one cl was given.");
        builder.AppendLine("A build whose records cannot be read is 'unmeasured' rather than passed, because a");
        builder.AppendLine("check that did not run has established nothing. A build directory that is not");
        builder.AppendLine("ninja's is skipped and says so.");
        builder.AppendLine();
        builder.AppendLine("A leg no host can run is a warning that names it and says why; the other legs");
        builder.AppendLine("still go ahead. The check fails when a leg named with --legs cannot run, or when");
        builder.AppendLine("no selected leg can - and, with exit 70, when whether a leg can run was never");
        builder.AppendLine("established, through a defect in this tool, named or not. --legs given without a");
        builder.AppendLine("name is refused, not taken for every leg.");
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
        builder.AppendLine("The ssh that runs is the first on the PATH. It is asked first what it would do, with");
        builder.AppendLine("ssh -G, and the name it would look up - the address declared, or a HostName its own");
        builder.AppendLine("configuration gives it - is looked up here, retrying; a name that does not resolve is");
        builder.AppendLine("refused before ssh starts. Every call the run makes is then given the address it");
        builder.AppendLine("resolved to as ssh's HostName, with the host's key still checked under the name, not");
        builder.AppendLine("the address. Each still goes to the address declared, so every Host block written for");
        builder.AppendLine("it applies. So a name that answers only now and then - a Mac in a dark wake - or an");
        builder.AppendLine("ssh that looks names up by other means - Git for Windows' own resolves no mDNS .local");
        builder.AppendLine("name - fails no run part way while that address keeps answering. Nothing is pinned");
        builder.AppendLine("where ssh reaches the host through a ProxyJump or a ProxyCommand, which do their own");
        builder.AppendLine("lookup, or where the address would change anything else ssh does; and an address that");
        builder.AppendLine("stops taking the connection, or shows a key the name is not known by, is dropped, and");
        builder.AppendLine("ssh looks the name up itself.");
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
        builder.AppendLine("which 'DssHarness sync' creates and keeps in step with this tree.");
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
        builder.AppendLine($"  {HarnessExit.Success,3}  legs: every named leg can run and every host answered");
        builder.AppendLine($"  {HarnessExit.Incomplete,3}  legs: the legs that answered can run, and a host did not answer");
        builder.AppendLine($"  {LegsExit.Unavailable,3}  legs: a leg named with --legs cannot run, or no selected leg can");
        builder.AppendLine($"  {HarnessExit.InternalError,3}  legs: whether a leg can run was never established, through a defect in this tool");
        builder.AppendLine($"  {HarnessExit.UsageError,3}  --legs names something that is neither a leg nor a leg set, or no name at all");
        builder.AppendLine($"  {HarnessExit.Refused,3}  a host runs a newer {ToolPackage.Id} than this machine");
        builder.AppendLine($"  {HarnessExit.HostUnavailable,3}  host-exec: the host cannot run {ToolPackage.Id}, has no copy of the repository,");
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
        builder.AppendLine("  <worktrees.root>/<name>");
        builder.AppendLine($"which is '{WorktreeSettings.DefaultRoot}' unless config.json names another. That");
        builder.AppendLine("root is configurable precisely because it is spent before a worktree's own name:");
        builder.AppendLine("a shorter one, such as '.worktrees', buys those characters back. Ask");
        builder.AppendLine("list-worktree where a worktree actually is rather than assuming the default.");
        builder.AppendLine("On Windows that path, plus build/<variant> for the longest variant this machine");
        builder.AppendLine("builds, plus the longest path the build system generates below that, must stay");
        builder.AppendLine($"under {HostPlatform.WindowsMaxPath} characters. Exceeding it does not fail as a path error: it appears");
        builder.AppendLine("as compile errors in files the worktree never touched.");
        builder.AppendLine();
        builder.AppendLine("create-worktree refuses up front when the budget cannot be met, and says how");
        builder.AppendLine("long a name would still fit. Both sides of the budget are in config.json:");
        builder.AppendLine("  worktrees.pathBudgetReserve   the longest path your build generates below its");
        builder.AppendLine("                                own build directory, relative to it; the check adds");
        builder.AppendLine("                                build/<variant> and every separator itself, and");
        builder.AppendLine("                                every build warns when it went deeper");
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
        builder.AppendLine("Ignored files outside a declared evidenceRoots directory are deleted unchecked,");
        builder.AppendLine("even ones no build makes again, such as .env, and so are ignored directories");
        builder.AppendLine("with everything in them, the history of a repository nested inside one and the");
        builder.AppendLine("records of every run started in the worktree included.");
        builder.AppendLine("A worktree whose evidenceRoots directory holds anything is refused;");
        builder.AppendLine("--delete-evidence waives that one check and nothing else. --force skips every");
        builder.AppendLine("check and overrides a lock; whatever the worktree held is lost.");
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
        builder.AppendLine("  check-anchor-citations --current-commit|--current-tree|--current-pr [--json]");
        builder.AppendLine("                                                    check every cited id resolves");
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
        builder.AppendLine("check-anchor-citations reads every file under anchors.citationRoots - as HEAD holds");
        builder.AppendLine("it, as the disk holds it, or only what this branch changed - and fails when a cited");
        builder.AppendLine("id resolves to no row in either registry, by the rule read-anchor finds a row by.");
        builder.AppendLine("A citation a wrapped line cut is reported as cut, whatever rows exist, where it runs");
        builder.AppendLine("into a hyphen that ends its line; one cut before it has two segments counts when the");
        builder.AppendLine("next line carries on with what makes it a citation. One ending its line where the");
        builder.AppendLine("next opens with a hyphen is cut where the two lines joined spell a row's id: a line");
        builder.AppendLine("opening with an option such as -Wall, a figure such as (-40, or a removed diff line");
        builder.AppendLine("carries no id on. A break inside a segment, with no hyphen on either side, cannot be");
        builder.AppendLine("told from a line that simply ends there: it reads as the shorter id, reported");
        builder.AppendLine("unresolved unless that shorter id is a row of its own. Keep every id whole on one line.");
        builder.AppendLine();
        builder.AppendLine("Every change holds a machine-wide lock on its two registries. A change that cannot");
        builder.AppendLine($"take it within {NamedMutexAnchorRegistryLock.DefaultTimeout.TotalSeconds:0} seconds writes nothing and exits {HarnessExit.Refused}.");
        builder.AppendLine();
        builder.AppendLine("Exit codes");
        builder.AppendLine($"  {AnchorExit.Findings,3}  an id was not found, --lint found problems, the balance did not hold,");
        builder.AppendLine("       or a citation resolves to no row or is cut");
        builder.AppendLine($"  {HarnessExit.UsageError,3}  a value is not valid (an id, a priority, a status, an empty trigger),");
        builder.AppendLine("       options that cannot be combined, or set-anchor with nothing to change");
        builder.AppendLine($"  {HarnessExit.NotInitialized,3}  a registry is missing; run '{ToolPackage.Command} init'");
        builder.AppendLine($"  {HarnessExit.Refused,3}  refused: the id exists, there is no such anchor, an id has two rows,");
        builder.AppendLine("       the registry is ignored by git, the lock is held, anchors.citationRoots");
        builder.AppendLine("       declares no root, or a file in a root is not named in UTF-8");
        builder.AppendLine($"  {HarnessExit.CommandFailed,3}  a registry is malformed, the base commit cannot be read, or git");
        builder.AppendLine("       lists a file it cannot read");
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
        builder.AppendLine($"  {HarnessLayout.RunnerActionsDirectoryRelative}/<name>/<name>.yml");
        builder.AppendLine("                                    tracked; one directory per action, holding its");
        builder.AppendLine("                                    steps and whatever those steps run");
        builder.AppendLine("  .harness-config/runner/.env/       contents ignored; values actions read");
        builder.AppendLine("  .harness-config/runner/.secrets/   contents ignored; secret values actions read");
        builder.AppendLine("  .harness-config/sshItems/<name>/   ignored, and written by you, not init:");
        builder.AppendLine("                                     .env (address, user, port), .key, known_hosts");
        builder.AppendLine("  .harness-config/wslDistros/<name>/ ignored; .env (distribution, credential)");
        builder.AppendLine("  .harness-config/worktrees/         ignored whole, with no placeholder; made by the");
        builder.AppendLine("                                     first create-worktree, not by init");
        builder.AppendLine("                                     (the default; worktrees.root moves it)");
        builder.AppendLine("  .harness-config/runs/              ignored; one directory of records per run, in");
        builder.AppendLine("                                     the tree that ran it");
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
        builder.AppendLine("block on later runs and leaving every other rule untouched. It then asks git which");
        builder.AppendLine("rule decides each path the block rules on, and names in a note any rule that turns");
        builder.AppendLine("one the other way: one git follows undoes the block there, and one the block");
        builder.AppendLine("overrules does nothing there - named only where taking it out would take from git");
        builder.AppendLine("nothing the harness keeps: no path the block rules on, none of its own files - its");
        builder.AppendLine("configuration, a placeholder, an action's files, an anchor registry - and no");
        builder.AppendLine("directory those are in, as git reads every ignore file the tree has, its");
        builder.AppendLine("info/exclude and configured excludes file among them. An allowlist's re-include of");
        builder.AppendLine("the slot a placeholder is kept in is overruled for the slot's contents and needed");
        builder.AppendLine("for the placeholder, since git never looks inside an excluded directory, so it is");
        builder.AppendLine("not named. A rule agreeing with the block is not named, however it is spelled.");
        builder.AppendLine("'.env', which many repositories ignore, is named: it takes the whole runner/.env");
        builder.AppendLine("directory, and git re-includes no placeholder from an excluded one. A rule");
        builder.AppendLine("re-including a directory the block ignores, such as each host's under sshItems, is");
        builder.AppendLine("named too, though git itself names none for the files below it. A path git will");
        builder.AppendLine("not answer about - one beyond a symbolic link - is named with git's reason, and the");
        builder.AppendLine("rest are still asked. worktrees and runs are ignored by name, with no trailing");
        builder.AppendLine("slash, so one kept on another disk through a link is ignored as the link it is.");
        builder.AppendLine();
        builder.AppendLine("init writes the tree it runs in, a worktree's own included: its configuration - a");
        builder.AppendLine("copy of the main checkout's, where the worktree was running on that one - its");
        builder.AppendLine(".gitignore and its placeholders. The main checkout is left as it is, but for an");
        builder.AppendLine("anchor registry git ignores, created there where it is missing.");
        builder.AppendLine();
        builder.AppendLine("Ignored state lives in the main checkout. A worktree receives the tracked part of");
        builder.AppendLine(".harness-config through git but never the ignored part, so connection data and the");
        builder.AppendLine("run lock resolve back to the originating checkout. A run's records are the");
        builder.AppendLine("exception: they belong to the tree that ran it, so a run started inside a worktree");
        builder.AppendLine("writes them there, and build, test and run name the directory in their output and");
        builder.AppendLine("as runDirectory in --json. Action files are tracked, so a worktree has its own and a");
        builder.AppendLine("runner acts on the tree it was asked about.");

        return builder.ToString();
    }

    private static string RenderConfig()
    {
        var builder = new StringBuilder();

        builder.AppendLine("config.json");
        builder.AppendLine();
        builder.AppendLine($"  defaults       buildCores and testCores ({HarnessDefaults.DefaultCores} each), maxParallelLegs (per");
        builder.AppendLine("                 machine) and maxParallelLegsTotal (the whole fleet), default project,");
        builder.AppendLine("                 stall bound");
        builder.AppendLine("  toolchains     compilers, as environment and cache variables (msvc, gcc, clang)");
        builder.AppendLine("  sanitizers     instrumentation overlays composed onto a build");
        builder.AppendLine("  buildConfigs   named configurations (debug, release, o1, o2)");
        builder.AppendLine("  projects       what to build, and with which adapter (cmake, dotnet, dart)");
        builder.AppendLine("  hosts          this machine (local), WSL distributions (wsl) and ssh hosts (ssh),");
        builder.AppendLine("                 each with its own core counts and environment when they differ");
        builder.AppendLine("  emulators      ways to run programs for another processor on a host: qemu,");
        builder.AppendLine("                 Rosetta, Prism");
        builder.AppendLine("  developerEnvironments  what a toolchain's legs start in, set up on the host");
        builder.AppendLine("                 that runs them: visualStudio runs that instance's vcvarsall.bat");
        builder.AppendLine("  legs           units of work: os + processor (+ emulator) + project + toolchain");
        builder.AppendLine("                 + config (+ sanitizer)");
        builder.AppendLine("  legSets        named groups of legs, selected with --legs like a leg");
        builder.AppendLine("  tools          external tools to verify and install");
        builder.AppendLine("  predefinedRunners  multi-phase procedures such as a corpus test or a benchmark");
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
        builder.AppendLine("A combination's directory is kept between builds, and its build system decides");
        builder.AppendLine("what to rebuild. It starts from clean only where one of these holds, and the");
        builder.AppendLine("leg's line says which, as 'rebuilt from clean:':");
        builder.AppendLine();
        builder.AppendLine("  - the build before it cannot be trusted: a phase spanned a clock step, the inputs");
        builder.AppendLine("    moved while it ran, something else used the directory, or an input could not");
        builder.AppendLine("    be read");
        builder.AppendLine("  - a compiler CMake identified is not what is at its path now: CMake identifies a");
        builder.AppendLine("    compiler once, so one updated in place is never identified again");
        builder.AppendLine("  - an input that changed since that build began is dated no later than the newest");
        builder.AppendLine("    file it left, which a build system ordering dates would miss; the line names");
        builder.AppendLine("    the input, that file and both dates");
        builder.AppendLine("  - nothing can say: no record of what it was built from, the files git tracks");
        builder.AppendLine("    could not be listed, or an input, its date or the directory could not be read");
        builder.AppendLine();
        builder.AppendLine("A change dated after that build is left to the build system, which rebuilds what");
        builder.AppendLine("it knows reads it: an output a custom command makes from a file it names in no");
        builder.AppendLine("DEPENDS is not remade. A project's rebuildableFormats says which files are inputs,");
        builder.AppendLine("by extension or whole name such as '.cpp' or 'CMakeLists.txt', in place of what");
        builder.AppendLine("its type reads, and a file with no extension always is one; an edit to any other");
        builder.AppendLine("file never discards a directory.");
        builder.AppendLine();
        builder.AppendLine("A host's env reaches every process a leg starts there - each build phase, the");
        builder.AppendLine("ninja that reads the build's dependency records, the test runner, each step of");
        builder.AppendLine("a runner - as the lowest layer, so everything more specific still says");
        builder.AppendLine("otherwise. The developer environment the leg's toolchain names is set up over");
        builder.AppendLine("it, on that host. From lowest to highest:");
        builder.AppendLine();
        builder.AppendLine("  build    host env, developer environment, then the variant's (toolchain, build");
        builder.AppendLine("           config, sanitizer, project)");
        builder.AppendLine("  test     host env, developer environment, then the test invocation's env");
        builder.AppendLine("  run      host env, developer environment, then the runner's values and secrets,");
        builder.AppendLine("           its env, the step's");
        builder.AppendLine();
        builder.AppendLine("Names compare ignoring case, as on Windows. A PATH set in a host's env is where");
        builder.AppendLine("that host finds every one of those programs, which no survey can see: none of");
        builder.AppendLine("them is required of the host before a leg starts, and each is the run's to find -");
        builder.AppendLine("but in a developer environment, whose PATH, built over the host's, is looked in");
        builder.AppendLine("before the leg starts.");
        builder.AppendLine("A host running a leg another machine sent it reads the section that machine");
        builder.AppendLine("names it by, never 'local', which in their shared file is the machine that sent it.");
        builder.AppendLine();
        builder.AppendLine("A host's keepAwake - [\"caffeinate\", \"-dimsu\", \"-w\", \"{pid}\"] on macOS - runs on that");
        builder.AppendLine("host while a leg's own work does, {pid} filled in with the DssHarness process");
        builder.AppendLine("running the leg, and is stopped when the work ends. One that cannot start, or");
        builder.AppendLine("ends early, is said and fails nothing: a sleep it did not prevent still marks the");
        builder.AppendLine("phase it interrupted suspect, as on a host that declares none. A host's compiler");
        builder.AppendLine("cache is its own variable in env, CCACHE_DIR for ccache; compilerCacheDirectory");
        builder.AppendLine("is retired, and refused where it is read.");
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
        builder.AppendLine("A test count countPattern reads that differs from the other legs of the same project");
        builder.AppendLine("and test set is marked on its own - 'test count differs', and testCountDiffers and");
        builder.AppendLine("testCountNote in --json - never as a timing, and never changes a verdict either. A");
        builder.AppendLine("test invocation's testSet declares a set that differs on purpose, such as a");
        builder.AppendLine("platform's own tests:");
        builder.AppendLine();
        builder.AppendLine("  \"test\": {");
        builder.AppendLine("    \"all\": { \"runner\": \"ctest\", \"successPattern\": \"...\", \"countPattern\": \"...\" },");
        builder.AppendLine("    \"windows\": { \"testSet\": \"windows\" }");
        builder.AppendLine("  }");
        builder.AppendLine();
        builder.AppendLine("Windows legs are then compared with each other, and every other leg with the rest.");
        builder.AppendLine();
        builder.AppendLine("'test --filter', '--exclude' and '--label' reach the runner through the invocation's");
        builder.AppendLine("filterArg, excludeArg and labelArg, so one set of options serves every runner. For");
        builder.AppendLine("ctest, '-R' chooses tests by name, '-L' chooses them by label and '-LE' leaves a label");
        builder.AppendLine("out; a label could otherwise be left out but never chosen. The filter chooses the tests");
        builder.AppendLine("to run, each --exclude leaves its tests out, and each --label names one more the tests");
        builder.AppendLine("to run must carry. A runner that does not leave out each of several exclusions given");
        builder.AppendLine("apart - ctest leaves out only what every -LE matches - declares excludeJoin, and several");
        builder.AppendLine("reach it as one value joined by it: '|' for ctest, which init seeds. Where ctest's args");
        builder.AppendLine("give its excludeArg themselves, the exclusions are added to those values instead,");
        builder.AppendLine("where they stand: to each -LE's, and to the last -E's. An empty excludeJoin gives each");
        builder.AppendLine("its own excludeArg over a join the shared section declares.");
        builder.AppendLine();
        builder.AppendLine("A leg on a host reached through WSL or ssh runs in that host's copy of the repository:");
        builder.AppendLine("the files the sync writes there from this tree, in a git repository of the host's own -");
        builder.AppendLine("one the sync made, or one it took over - whose index and history are not this");
        builder.AppendLine("checkout's. A test that checks this checkout's state has nothing to say about that");
        builder.AppendLine("copy, so an invocation's remoteExcludes are given to every leg a host runs, beside");
        builder.AppendLine("--exclude's, and to none this machine runs:");
        builder.AppendLine();
        builder.AppendLine("  \"all\": {");
        builder.AppendLine("    \"runner\": \"ctest\", \"successPattern\": \"...\",");
        builder.AppendLine("    \"excludeArg\": \"-LE\", \"excludeJoin\": \"|\", \"remoteExcludes\": [\"git-state\"]");
        builder.AppendLine("  }");
        builder.AppendLine();
        builder.AppendLine("They are held to every rule --exclude's are, when the file is read, where the");
        builder.AppendLine("invocation alone decides it: ctest needs the excludeJoin, since --exclude's and its");
        builder.AppendLine("args' own reach it beside them. A test preset's filters are read as the leg starts.");
        builder.AppendLine();
        builder.AppendLine("ctest is refused an option it would read otherwise: an exclusion beside another given");
        builder.AppendLine("apart with no join; a filter beside a -R its args give; a filter or an exclusion beside");
        builder.AppendLine("a test preset that sets the same filter itself; and any of the three beside");
        builder.AppendLine("--rerun-failed, beside --union in the args - but an exclusion by -E, which ctest still");
        builder.AppendLine("reads there - or beside a test preset that takes a union or cannot be read. A label");
        builder.AppendLine("beside labels the args or a preset choose runs: ctest reads it as one more a test must");
        builder.AppendLine("carry. An option given where its arg is not declared, or given empty or as spaces");
        builder.AppendLine("alone, is refused, and a host running one of the legs is given all three. A -I in the");
        builder.AppendLine("args, or a preset's index, picks by position among the tests the others leave, so a");
        builder.AppendLine("filter, an exclusion or a label moves a fixed window onto other tests.");
        builder.AppendLine();
        builder.AppendLine("The file is checked when it is read: unknown keys and references to undeclared");
        builder.AppendLine("names are rejected, with every problem listed at once. Comments and trailing");
        builder.AppendLine("commas are accepted, since the file is meant to be edited. A section whose");
        builder.AppendLine("command is not implemented yet is still checked, but has no effect until that");
        builder.AppendLine("command arrives.");

        return builder.ToString();
    }
}
