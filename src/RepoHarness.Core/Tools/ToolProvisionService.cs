using System.Globalization;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Secrets;

namespace RepoHarness.Core.Tools;

/// <summary>
/// Brings every tool a selected leg needs onto the host that leg runs on, and reports what it found.
/// </summary>
/// <remarks>
/// The behaviour lives here rather than in the command so that <c>init</c> and the leg commands can
/// use it too: a host that has to be provisioned before anything runs is exactly the host a build
/// would otherwise fail on, with a message about a compiler rather than about a missing SDK.
/// </remarks>
public interface IToolProvisionService
{
    /// <summary>
    /// Provisions the legs <paramref name="legNames"/> selects, or every declared leg when
    /// <c>--legs</c> was left out and <paramref name="legNames"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="directory">A directory in the repository whose configuration declares the legs.</param>
    /// <param name="legNames">What <c>--legs</c> was given, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the run; a host already being installed on is left to finish its command.</param>
    Task<ToolProvisionReport> ProvisionAsync(
        string directory,
        IReadOnlyList<string>? legNames,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IToolProvisionService"/>
public sealed class ToolProvisionService(
    IHarnessContextLoader contextLoader,
    IHostConnector connector,
    IHostCommandRunner hostCommands,
    IHostProgramResolver programs,
    IHarnessOutput output,
    ISuperuserPrompt prompt) : IToolProvisionService
{
    /// <summary>The command's name, which prefixes what it reports.</summary>
    public const string CommandName = "install-missing-tools";

    /// <summary>Longest one probe of a host may take.</summary>
    public static readonly TimeSpan ProbeBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Longest one install may take. Generous because it includes a download over whatever link the
    /// host has, and a package manager that has to refresh its index first.
    /// </summary>
    public static readonly TimeSpan InstallBudget = TimeSpan.FromMinutes(30);

    /// <summary>Longest a regular expression may spend reading a version out of a tool's own output.</summary>
    private static readonly TimeSpan PatternBudget = TimeSpan.FromSeconds(2);

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostConnector _connector = connector;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHostProgramResolver _programs = programs;
    private readonly IHarnessOutput _output = output;
    private readonly ISuperuserPrompt _prompt = prompt;

    public async Task<ToolProvisionReport> ProvisionAsync(
        string directory,
        IReadOnlyList<string>? legNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var context = await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
        var config = context.Config;
        var selection = LegSelection.Resolve(config, legNames);

        // A leg is provisioned on the host it names, and on this machine when it names none: the first
        // of its candidates is exactly that. Nothing has been measured yet, so the host a leg would
        // fall through to cannot be known here, and installing on every candidate would install on
        // machines the leg will never touch.
        var placed = selection.Legs
            .Select(leg => (Leg: leg, Host: LegPlacement.Candidates(config, leg.Leg)[0]))
            .ToList();

        var done = new Dictionary<HostId, LegProvision>();
        var report = new List<LegProvision>();

        foreach (var (leg, host) in placed)
        {
            if (!done.TryGetValue(host, out var provision))
            {
                provision = await ProvisionHostAsync(context, host, cancellationToken).ConfigureAwait(false);
                done[host] = provision;
            }

            report.Add(provision with { Leg = leg.Name });
        }

        return new ToolProvisionReport(report);
    }

    /// <summary>Provisions one host, which may carry several legs.</summary>
    private async Task<LegProvision> ProvisionHostAsync(HarnessContext context, HostId host, CancellationToken cancellationToken)
    {
        var config = context.Config;

        // Only the harness's own SDK is looked for while connecting. The declared tools are looked
        // for once the host's platform is known, in the directories this repository declares for that
        // platform: the same list the survey and a leg's own run use there. Where the host's shell
        // cannot look in one of them, the tool is unknown rather than missing, so this command never
        // installs a second copy of a tool a leg there can already start.
        IReadOnlyList<string> wanted = host.Kind == HostKind.Local ? [] : [HostInspector.DotnetProgram];

        var opened = await _connector.ConnectAsync(context, host, wanted, cancellationToken).ConfigureAwait(false);

        if (opened.Connection is not { } connection)
        {
            return Unreachable(host, opened.Problem!);
        }

        try
        {
            return await ProvisionConnectedAsync(host, connection, opened, config, context, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (HostConnector.Unreached(ex) is { } reason)
        {
            // Its transport stopped starting part way through: this host is named, and the others
            // are still provisioned.
            return Unreachable(host, reason);
        }
    }

    /// <summary>A host that could not be reached, named, and what provisioning it reports.</summary>
    private LegProvision Unreachable(HostId host, string reason)
    {
        _output.Warn(CommandName, $"{host} could not be reached: {reason}");
        return new LegProvision { Leg = string.Empty, Host = host, Unreachable = reason };
    }

    /// <summary>Provisions a host that was reached.</summary>
    private async Task<LegProvision> ProvisionConnectedAsync(
        HostId host,
        HostConnection connection,
        HostConnectionResult opened,
        HarnessConfig config,
        HarnessContext context,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<ToolOutcome>();
        var superuser = new Superuser(host, opened.Superuser, ItemEnvFile(context.Layout, host));

        // .NET is the default tool on every host reached through a transport: a host that cannot run
        // DssHarness is a host with no SDK, and every other command begins by running DssHarness there.
        if (host.Kind != HostKind.Local)
        {
            var (outcome, updated) = await ProvisionDotnetAsync(host, connection, cancellationToken).ConfigureAwait(false);
            connection = updated;
            outcomes.Add(outcome);
        }

        var platformKey = await PlatformKeyAsync(connection, opened.Os, cancellationToken).ConfigureAwait(false);
        var searched = ToolSearchDirectories.For(config.ToolSearchDirectories, platformKey);

        connection = await _programs
            .ResolveAsync(connection, [.. config.Tools.Select(tool => tool.Name)], searched, ProbeBudget, cancellationToken)
            .ConfigureAwait(false);

        // A tool this platform does not need is not probed here, rather than probed and excused.
        // Probing costs a round trip to the host for an answer nothing would read, and an outcome
        // recorded for it would have to be excused again by everything that counts outcomes.
        foreach (var tool in config.Tools.Where(tool => PlatformScope.Applies(tool.Platforms, platformKey)))
        {
            var (outcome, updated) = await ProvisionToolAsync(host, connection, tool, platformKey, searched, superuser, cancellationToken)
                .ConfigureAwait(false);

            connection = updated;
            outcomes.Add(outcome);
        }

        // Every reason a host gave is scrubbed at this one point rather than where it was built: a host
        // that echoes what it was handed does so from whichever command it likes, and a credential that
        // reached one report would be in every log that kept it.
        return new LegProvision
        {
            Leg = string.Empty,
            Host = host,
            Tools = [.. outcomes.Select(outcome => outcome.Detail is { } detail
                ? outcome with { Detail = superuser.Hide(detail) }
                : outcome)],
        };
    }

    /// <summary>
    /// Installs the .NET SDK on a host that has none, into the home directory, where it needs no
    /// superuser. The installer puts it where no login-free PATH names it, which is why every call
    /// site spells the resolved path instead of the bare name.
    /// </summary>
    private async Task<(ToolOutcome Outcome, HostConnection Connection)> ProvisionDotnetAsync(
        HostId host,
        HostConnection connection,
        CancellationToken cancellationToken)
    {
        const string Name = HostInspector.DotnetProgram;
        var needed = $".NET {ToolPackage.MinimumSdkMajor}";

        if (connection.Located(Name) is { Found: ProgramFound.Unreadable } unreadable)
        {
            return (new ToolOutcome(Name, ToolState.Unknown, null, unreadable.WhyUnestablished()), connection);
        }

        if (await HighestSdkAsync(connection, cancellationToken).ConfigureAwait(false) is { } current)
        {
            return (new ToolOutcome(Name, ToolState.AlreadyCurrent, current), connection);
        }

        // The installer is a shell script, and a Windows host has no shell to run it. Said plainly
        // rather than attempted: a half-run installer leaves a host worse than an untouched one.
        if (connection.Shell == RemoteShell.Cmd)
        {
            return (
                new ToolOutcome(Name, ToolState.Failed, null,
                    $"the {needed} SDK is installed there with the Windows installer, which this command cannot run; "
                    + $"install it on that host and run '{CommandName}' again"),
                connection);
        }

        _output.Info(CommandName, $"{host}: installing the {needed} SDK under the home directory");

        // The script travels on standard input, so no part of it has to survive a shell's word
        // splitting on the way: an ssh command line carries only words every shell reads literally.
        var install = await RunAsync(
            connection,
            "sh",
            [],
            InstallBudget,
            cancellationToken,
            InstallScript).ConfigureAwait(false);

        if (!install.Succeeded)
        {
            return (
                new ToolOutcome(Name, ToolState.Failed, null, HostProbes.Failure($"installing the {needed} SDK there failed", install)),
                connection);
        }

        // Measured again rather than assumed: the installer's own directory is what the next command
        // has to spell, and the run that put it there is the only one that can find out where it went.
        connection = await _programs
            .ResolveAsync(connection.Forget(Name), [Name], ToolSearchDirectories.Posix, ProbeBudget, cancellationToken)
            .ConfigureAwait(false);

        var installed = await HighestSdkAsync(connection, cancellationToken).ConfigureAwait(false);

        return installed is null
            ? (new ToolOutcome(Name, ToolState.Failed, null,
                $"the {needed} SDK installer reported success, and '{Name}' still does not list an SDK "
                + $"{ToolPackage.MinimumSdkMajor} or newer there"), connection)
            : (new ToolOutcome(Name, ToolState.Installed, installed), connection);
    }

    /// <summary>
    /// The newest SDK on the host that is new enough to run DssHarness, or <see langword="null"/> when
    /// there is none, <c>dotnet</c> is not there, or it would not run.
    /// </summary>
    private async Task<string?> HighestSdkAsync(HostConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Located(HostInspector.DotnetProgram) is not { Present: true })
        {
            return null;
        }

        var sdks = await RunAsync(
            connection,
            connection.Spell(HostInspector.DotnetProgram),
            ["--list-sdks"],
            ProbeBudget,
            cancellationToken).ConfigureAwait(false);

        return sdks.Succeeded
            ? HostProbes.ReadSdks(sdks.StandardOutput)
                .Where(sdk => sdk.Major >= ToolPackage.MinimumSdkMajor)
                .Select(sdk => sdk.Version)
                .LastOrDefault()
            : null;
    }

    /// <summary>Probes one declared tool, and installs or updates it when it declares how.</summary>
    private async Task<(ToolOutcome Outcome, HostConnection Connection)> ProvisionToolAsync(
        HostId host,
        HostConnection connection,
        ToolConfig tool,
        string? platformKey,
        IReadOnlyList<string> searched,
        Superuser superuser,
        CancellationToken cancellationToken)
    {
        var located = connection.Located(tool.Name) ?? new ProgramLocation(tool.Name, ProgramFound.Unreadable);

        if (located.Found == ProgramFound.Unreadable)
        {
            return (new ToolOutcome(tool.Name, ToolState.Unknown, null, located.WhyUnestablished()), connection);
        }

        var install = InstallFor(tool, platformKey);

        if (!located.Present)
        {
            // An entry with no install is an allowlist entry: it says the tool may be used, not how to
            // obtain it, so it is reported rather than guessed at.
            if (install is null)
            {
                return (new ToolOutcome(tool.Name, ToolState.Missing, null, Needed(tool, platformKey)), connection);
            }

            return await RunInstallAsync(host, connection, tool, install, searched, superuser, update: false, cancellationToken)
                .ConfigureAwait(false);
        }

        var (version, problem) = await ProbeVersionAsync(connection, tool, cancellationToken).ConfigureAwait(false);

        if (problem is not null)
        {
            return (new ToolOutcome(tool.Name, ToolState.Unknown, version, problem), connection);
        }

        if (tool.MinVersion is not { Length: > 0 } minimum)
        {
            return (new ToolOutcome(tool.Name, ToolState.AlreadyCurrent, version), connection);
        }

        if (!SemanticVersion.TryParse(version, out var found) || !SemanticVersion.TryParse(minimum, out var least))
        {
            // Biased toward not knowing: reported rather than passed off as current, because a tool
            // whose version could not be read is exactly the one whose age nobody can vouch for.
            return (
                new ToolOutcome(tool.Name, ToolState.Unknown, version, Uncomparable(version, minimum)),
                connection);
        }

        if (SemanticVersion.Compare(found, least) >= 0)
        {
            return (new ToolOutcome(tool.Name, ToolState.AlreadyCurrent, version), connection);
        }

        if (install is null)
        {
            return (
                new ToolOutcome(tool.Name, ToolState.Outdated, version,
                    $"it is {version}, and at least {minimum} is needed; it declares no install, so update it there by hand"),
                connection);
        }

        return await RunInstallAsync(host, connection, tool, install, searched, superuser, update: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs one tool's install, then measures the host again rather than believing it.</summary>
    private async Task<(ToolOutcome Outcome, HostConnection Connection)> RunInstallAsync(
        HostId host,
        HostConnection connection,
        ToolConfig tool,
        ToolInstall install,
        IReadOnlyList<string> searched,
        Superuser superuser,
        bool update,
        CancellationToken cancellationToken)
    {
        if (ToolCommands.For(install, update) is not { Count: > 0 } command)
        {
            return (
                new ToolOutcome(tool.Name, ToolState.Failed, null,
                    $"its install declares neither a command nor a manager this build knows; known managers are "
                    + $"{string.Join(", ", ToolCommands.KnownManagers)}"),
                connection);
        }

        var privileged = await superuser.PrepareAsync(this, connection, command, cancellationToken).ConfigureAwait(false);

        if (privileged.Problem is { } refused)
        {
            return (new ToolOutcome(tool.Name, ToolState.Failed, null, refused), connection);
        }

        _output.Info(CommandName, $"{host}: {(update ? "updating" : "installing")} '{tool.Name}' with {privileged.Command[0]}");

        var result = await RunAsync(
            connection,
            privileged.Command[0],
            [.. privileged.Command.Skip(1)],
            InstallBudget,
            cancellationToken,
            privileged.Input).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return (
                new ToolOutcome(tool.Name, ToolState.Failed, null,
                    superuser.Hide(HostProbes.Failure($"{(update ? "updating" : "installing")} '{tool.Name}' there failed", result))),
                connection);
        }

        connection = await _programs
            .ResolveAsync(connection.Forget(tool.Name), [tool.Name], searched, ProbeBudget, cancellationToken)
            .ConfigureAwait(false);

        if (connection.Located(tool.Name) is not { Present: true })
        {
            return (
                new ToolOutcome(tool.Name, ToolState.Failed, null,
                    $"its install reported success, and '{tool.Name}' is still neither on the PATH of a command run "
                    + "without a login shell nor in any of the directories an installer uses"),
                connection);
        }

        var (version, problem) = await ProbeVersionAsync(connection, tool, cancellationToken).ConfigureAwait(false);

        if (problem is not null)
        {
            // The install reported success and the tool is there; what nobody can say is which
            // version arrived. Reported as unknown rather than installed: "it is current" is a claim
            // about a number this run never read.
            return (new ToolOutcome(tool.Name, ToolState.Unknown, version, problem), connection);
        }

        // The same comparison the probe makes before installing. An installer can report success and
        // leave an older version in place — a package pinned by a distribution, a cached artefact, a
        // second copy earlier on the PATH — and reporting that as up to date is how a leg runs for
        // weeks against a toolchain the configuration says it is not using.
        if (tool.MinVersion is { Length: > 0 } minimum)
        {
            if (!SemanticVersion.TryParse(version, out var found) || !SemanticVersion.TryParse(minimum, out var least))
            {
                return (
                    new ToolOutcome(tool.Name, ToolState.Unknown, version,
                        $"its install reported success, and '{version ?? string.Empty}' there cannot be compared "
                        + $"with the minVersion '{minimum}'"),
                    connection);
            }

            if (SemanticVersion.Compare(found, least) < 0)
            {
                return (
                    new ToolOutcome(tool.Name, ToolState.Failed, version,
                        $"its install reported success, and it is still {version}, where at least {minimum} is needed"),
                    connection);
            }
        }

        return (new ToolOutcome(tool.Name, update ? ToolState.Updated : ToolState.Installed, version), connection);
    }

    /// <summary>Reads a tool's version the way its own configuration says to read it.</summary>
    /// <returns>The version, and why it could not be read when it could not.</returns>
    private async Task<(string? Version, string? Problem)> ProbeVersionAsync(
        HostConnection connection,
        ToolConfig tool,
        CancellationToken cancellationToken)
    {
        if (tool.Probe is not { } probe)
        {
            return (null, null);
        }

        var result = await RunAsync(connection, connection.Spell(tool.Name), probe.Args, ProbeBudget, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return (null, HostProbes.Failure($"'{tool.Name}' would not report its version there", result));
        }

        var text = result.StandardOutput.Length > 0 ? result.StandardOutput : result.StandardError;

        if (probe.Regex is not { Length: > 0 } pattern)
        {
            // With no pattern, the last word of the first line is the version a tool usually prints:
        // "ninja version 1.12.1", "git version 2.47.0".
            return (text.Split('\n').FirstOrDefault()?.Trim().Split(' ').LastOrDefault(), null);
        }

        try
        {
            var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant, PatternBudget);

            return match.Success
                ? (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value, null)
                : (null, $"the probe regex of '{tool.Name}' matched nothing in what it printed: {HostProbes.Excerpt(text)}");
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return (null, $"the probe regex of '{tool.Name}' could not be used: {ex.Message}");
        }
    }

    /// <summary>
    /// Which operating system's install entry applies. Measured rather than taken from this machine: an
    /// ssh host is usually not the same system as the one reaching it, and the entry chosen decides
    /// which package manager runs.
    /// </summary>
    /// <summary>
    /// The platform a host's tools are chosen for, or <see langword="null"/> when it could not be
    /// established.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess, because a guess here decides which tools a host is asked about at
    /// all. A host that answered <c>uname</c> with a system this build has no name for is some
    /// POSIX machine; calling it Windows would skip every tool scoped to Linux and then report the
    /// leg as having everything it needs. Unknown instead means every tool is probed, which costs a
    /// round trip and tells the truth.
    /// </remarks>
    private async Task<string?> PlatformKeyAsync(HostConnection connection, string? measured, CancellationToken cancellationToken)
    {
        if (measured is { Length: > 0 })
        {
            return measured;
        }

        if (connection.Shell == RemoteShell.Cmd)
        {
            return PlatformNames.Windows;
        }

        var uname = await RunAsync(connection, "uname", ["-s"], ProbeBudget, cancellationToken).ConfigureAwait(false);

        // A host that did not answer at all - it timed out, or ssh itself failed - said nothing about
        // what it is. Read as Windows, it would be searched in no POSIX directory and asked about no
        // tool scoped to Linux, and then reported as having everything it needs.
        if (!HostProbes.Answered(uname))
        {
            return null;
        }

        // uname is on every POSIX system, so a host that answered without it is a Windows one running
        // PowerShell, which is the only other shell an ssh server here hands a command to. A host that
        // has it and names a system this build does not know is not that, and is not guessed at.
        return uname.Succeeded ? PlatformNames.ForKernel(uname.TrimmedOutput) : PlatformNames.Windows;
    }

    /// <summary>
    /// Why a version and a minimum could not be compared, naming whichever of them failed to parse.
    /// </summary>
    /// <remarks>
    /// The two fail for unrelated reasons and have unrelated remedies: what the host reported is
    /// the probe's business, and the minimum is a string in this repository's own configuration.
    /// One message for both sent every reader of a two-component minVersion — which this parser
    /// refuses, because the field is a semantic version — to adjust a probe regex that had just
    /// worked perfectly.
    /// </remarks>
    /// <param name="version">What the probe read from the host, if anything.</param>
    /// <param name="minimum">The configured minVersion.</param>
    private static string Uncomparable(string? version, string? minimum)
    {
        var foundParses = SemanticVersion.TryParse(version, out _);
        var leastParses = SemanticVersion.TryParse(minimum, out _);

        return (foundParses, leastParses) switch
        {
            (true, false) =>
                $"the minVersion '{minimum}' is not a version this can compare; "
                + "write it as major.minor.patch, such as '1.2.0'",
            (false, true) =>
                $"'{version ?? string.Empty}' is not a version this can compare with the minVersion "
                + $"'{minimum}'; give the tool's probe a regex whose first group is the version",
            _ =>
                $"neither '{version ?? string.Empty}' nor the minVersion '{minimum}' is a version this "
                + "can compare; the minVersion is written as major.minor.patch, and the probe's regex "
                + "captures the version in its first group",
        };
    }

    /// <summary>The install entry for one platform, falling back to the one declared for <c>all</c>.</summary>
    private static ToolInstall? InstallFor(ToolConfig tool, string? platformKey)
        => PlatformScope.Select(tool.Install, platformKey);

    /// <summary>What a tool that is not there, and cannot be installed, is reported as.</summary>
    private static string Needed(ToolConfig tool, string? platformKey)
    {
        var why = tool.Why is { Length: > 0 } purpose ? $" ({purpose})" : string.Empty;
        var platform = platformKey is { Length: > 0 } measured
            ? $"'{measured}'"
            : "this host, whose operating system could not be established,";

        return tool.Install.Count == 0
            ? $"it is not installed there{why}; nothing declares how to install it, so install it by hand"
            : $"it is not installed there{why}; its install declares nothing for {platform} or for 'all'";
    }

    private async Task<ProcessResult> RunAsync(
        HostConnection connection,
        string program,
        IReadOnlyList<string> arguments,
        TimeSpan budget,
        CancellationToken cancellationToken,
        string input = "")
    {
        try
        {
            return await _hostCommands.RunAsync(
                connection,
                new HostCommand
                {
                    Program = program,
                    Arguments = arguments,
                    Timeout = budget,
                    StandardInput = input,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or ExecutableNotFoundException)
        {
            // A token no shell reads literally, or a program of this machine's own that is not
            // installed: reported as what the command did, because either way the program never ran.
            return new ProcessResult(-1, string.Empty, ex.Message, TimeSpan.Zero, TimedOut: false);
        }
    }

    /// <summary>
    /// The script that installs the .NET SDK under the home directory. Run by a shell reading it from
    /// standard input, so nothing in it has to be a word an ssh command line can carry.
    /// </summary>
    /// <remarks>
    /// The outer script is plain POSIX and runs under whatever <c>/bin/sh</c> the host has.
    /// <c>dotnet-install.sh</c> is not: it sets <c>-o pipefail</c>, which dash — <c>/bin/sh</c> on
    /// Debian and Ubuntu — refuses, and the install then fails with "Illegal option -o pipefail"
    /// rather than anything about a shell. So it is handed to <c>bash</c> by name, and a host
    /// without bash is told that plainly instead of being shown that message.
    /// </remarks>
    private static string InstallScript => $"""
        set -e
        if ! command -v bash >/dev/null 2>&1; then
          echo "{BashMissing}" >&2
          exit 1
        fi
        script="$HOME/.dotnet-install.sh"
        if command -v curl >/dev/null 2>&1; then
          curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script"
        else
          wget -qO "$script" https://dot.net/v1/dotnet-install.sh
        fi
        bash "$script" --channel {ToolPackage.MinimumSdkMajor.ToString(CultureInfo.InvariantCulture)}.0 --install-dir "$HOME/.dotnet" --no-path
        rm -f "$script"

        """;

    /// <summary>
    /// What the host prints when it has no bash, quoted back by the refusal so the reader is told
    /// what to install rather than shown a shell error about an option they never wrote.
    /// </summary>
    internal const string BashMissing =
        "bash is required to install the .NET SDK (dotnet-install.sh uses bash features that /bin/sh may not have); install bash on this host and run install-missing-tools again";

    /// <summary>
    /// The <c>.env</c> a host's superuser credential belongs in, spelt as this repository spells it, or
    /// <see langword="null"/> for this machine, which has no item and no credential to declare.
    /// </summary>
    private static string? ItemEnvFile(HarnessLayout layout, HostId host)
    {
        if (host.Kind == HostKind.Local)
        {
            return null;
        }

        var directory = host.Kind == HostKind.Wsl ? layout.WslDistroDirectory(host.Name) : layout.SshItemDirectory(host.Name);

        return Path.GetRelativePath(layout.MainCheckoutRoot, Path.Combine(directory, HarnessLayout.ItemEnvFileName))
            .Replace('\\', '/');
    }

    /// <summary>
    /// How a privileged command is run on one host, measured once and then reused. The credential
    /// travels on standard input and nowhere else: an argument list is visible in that host's own
    /// process table to every user on it, and a log of one outlives the run.
    /// </summary>
    /// <param name="host">The host, named in the prompt so two of them are never confused.</param>
    /// <param name="credential">The credential the host's item declares, when it declares one.</param>
    /// <param name="itemEnvFile">Where a credential would be declared, named when one is needed and absent.</param>
    private sealed class Superuser(HostId host, HostCredential? credential, string? itemEnvFile)
    {
        /// <summary>
        /// What sudo exits with when it read a password and would not accept it. Any other ending is
        /// the check not happening rather than the password being wrong.
        /// </summary>
        private const int PasswordRefused = 1;

        private bool? _passwordless;
        private HostCredential? _typed;
        private HostCredential? _accepted;
        private bool _asked;
        private string? _settled;

        /// <summary>
        /// The ways this host could be given a password, or do without one. Running as root is named
        /// last and always, because it is the answer for a run with nobody to ask: a job that installs
        /// its own dependencies in an earlier step usually already runs that way.
        /// </summary>
        private string Declared => itemEnvFile is null
            ? "let this user run sudo without a password, run the harness as root, which needs none, "
                + "or install it by hand"
            : $"add '{HostSecretsStore.SuperuserKey}' to '{itemEnvFile}', let that user run sudo without "
                + "a password, or run the harness there as root, which needs none";

        /// <summary>What a privileged command becomes, and what it is given on standard input.</summary>
        /// <param name="service">The service, which owns how a command is run on a host.</param>
        /// <param name="connection">The host.</param>
        /// <param name="command">The command as the configuration declares it.</param>
        /// <param name="cancellationToken">Stops the probe.</param>
        public async Task<(IReadOnlyList<string> Command, string Input, string? Problem)> PrepareAsync(
            ToolProvisionService service,
            HostConnection connection,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(command[0], "sudo", StringComparison.Ordinal))
            {
                return (command, string.Empty, null);
            }

            // One declared for this host, or one typed for it earlier in this run. Never one typed for
            // another host: each host has its own Superuser, and there is no collection joining them.
            if ((credential ?? _accepted) is { } known)
            {
                // -S makes sudo read the password from standard input instead of from a terminal, which
                // a command started this way does not have at all.
                return ([command[0], "-S", .. command.Skip(1)], known.Reveal() + "\n", null);
            }

            // A password this host already rejected is not offered again for the next tool on it. One
            // mistyped password should cost one failed authentication there, not one for every tool.
            if (_settled is { } refused)
            {
                return (command, string.Empty, refused);
            }

            _passwordless ??= (await service
                .RunAsync(connection, "sudo", ["-n", "true"], ProbeBudget, cancellationToken)
                .ConfigureAwait(false)).Succeeded;

            if (_passwordless.Value)
            {
                return ([command[0], "-n", .. command.Skip(1)], string.Empty, null);
            }

            if (!_asked && service._prompt.Availability == PromptAvailability.Available)
            {
                // Asked once for this host however many tools on it need one, which is what makes a
                // second privileged tool silent rather than a second interruption.
                _asked = true;

                if (await TypedAsync(service, connection, cancellationToken).ConfigureAwait(false) is { } answer)
                {
                    return answer.Accepted
                        ? ([command[0], "-S", .. command.Skip(1)], answer.Said, null)
                        : (command, string.Empty, answer.Said);
                }
            }

            return (
                command,
                string.Empty,
                $"it has to be installed by a superuser, and this run has no password for one; {Declared}{Unasked(service)}");
        }

        /// <summary>
        /// Asks for a password and finds out whether this host accepts it, or <see langword="null"/>
        /// when nothing was typed. Checked before an install that may run for half an hour rides on it,
        /// and checked once: a second guess is a second failed authentication counted against that
        /// account, and some hosts count those toward locking it.
        /// </summary>
        /// <returns>
        /// Whether the host accepted it, and either what its standard input should carry or why the
        /// tool is refused.
        /// </returns>
        private async Task<(bool Accepted, string Said)?> TypedAsync(
            ToolProvisionService service,
            HostConnection connection,
            CancellationToken cancellationToken)
        {
            var typed = await service._prompt.ReadPasswordAsync(host, cancellationToken).ConfigureAwait(false);

            if (typed.Length == 0)
            {
                return null;
            }

            // Kept whether or not it turns out to be right: a wrong one still travelled to the host, and
            // Hide is what stops anything the host echoes back from carrying it into a report.
            _typed = new HostCredential(typed);

            var answer = await service
                .RunAsync(connection, "sudo", ["-S", "-v"], ProbeBudget, cancellationToken, _typed.Reveal() + "\n")
                .ConfigureAwait(false);

            if (answer.Succeeded)
            {
                _accepted = _typed;
                return (true, _accepted.Reveal() + "\n");
            }

            // A check that never finished is not a password the host refused. A probe that ran out of
            // time, a host that stopped answering, a transport that failed: calling any of those a
            // wrong password blames somebody's typing for a machine that went away, and this verdict
            // is then remembered for every later tool on that host.
            if (!answer.TimedOut && answer.ExitCode == PasswordRefused)
            {
                // What sudo said is deliberately not quoted back. It is the one message on this path
                // that could hold the password itself, and a fixed refusal cannot.
                _settled = $"it has to be installed by a superuser, and the password typed for {host} was not "
                    + $"accepted there; run '{CommandName}' again to try another, or {Declared}";
            }
            else
            {
                _settled = Hide(HostProbes.Failure(
                    $"it has to be installed by a superuser, and checking the password typed for {host} never finished",
                    answer));
            }

            return (false, _settled);
        }

        /// <summary>Why nobody was asked, said only when somebody could have been.</summary>
        private static string Unasked(ToolProvisionService service)
            => service._prompt.Availability == PromptAvailability.Suppressed
                ? "; without --no-prompt this would have asked for one"
                : string.Empty;

        /// <summary>
        /// Text with the credential taken out of it, applied to everything a host printed before it can
        /// reach a report. sudo does not echo a password, and a command that logs its own input does.
        /// </summary>
        public string Hide(string text) => Scrub(Scrub(text, credential), _typed);

        /// <summary>
        /// One secret taken out of text. A typed password is scrubbed even when this host rejected it:
        /// it still reached that host on standard input, and a host that echoes what it was given does
        /// not first check whether the password worked.
        /// </summary>
        private static string Scrub(string text, HostCredential? secret)
            => secret is null || secret.Reveal().Length == 0
                ? text
                : text.Replace(secret.Reveal(), secret.ToString(), StringComparison.Ordinal);
    }
}
