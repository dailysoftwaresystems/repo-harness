namespace RepoHarness.Core.Configuration;

/// <summary>An external tool this repository needs in order to work.</summary>
public sealed class ToolConfig
{
    /// <summary>Executable name, used both to probe for it and to report on it.</summary>
    public required string Name { get; init; }

    /// <summary>How to read the installed version.</summary>
    public ToolProbe? Probe { get; init; }

    /// <summary>Lowest acceptable version; below it the tool is updated.</summary>
    public string? MinVersion { get; init; }

    /// <summary>
    /// Platforms this tool is needed on (<c>windows</c>, <c>linux</c>, <c>macos</c>) or <c>all</c>.
    /// </summary>
    /// <remarks>
    /// A host whose platform this does not name is never asked about the tool, so it is neither
    /// probed there nor counted against that host's legs. Without it every tool was needed
    /// everywhere: a repository declaring MSVC and a Linux compiler could not have both, because
    /// each was reported missing on the other's hosts and no leg was ever fully provisioned. Left
    /// out, a tool is needed on every platform, which is what every list written before this meant.
    /// </remarks>
    public List<string> Platforms { get; init; } = [];

    /// <summary>
    /// Toolchains this tool is needed for: only a leg whose variant builds with one of them needs it.
    /// Left out, every toolchain.
    /// </summary>
    /// <remarks>
    /// The narrower axis two legs of one operating system differ on. <c>cl</c> scoped to Windows alone
    /// was reported missing on a MinGW leg, because that leg is Windows too.
    /// </remarks>
    public List<string> Toolchains { get; init; } = [];

    /// <summary>Legs or leg sets this tool is needed for, by name. Left out, every leg.</summary>
    public List<string> Legs { get; init; } = [];

    /// <summary>
    /// Processors this tool is needed for: the processor a leg is built for, which under an emulator
    /// is not the host's. Left out, every processor.
    /// </summary>
    /// <remarks>
    /// The leg's, because that is the one every leg declares: an arm64 leg on an x86_64 host is still
    /// an arm64 leg. So this reaches a native arm64 host as surely as an emulated leg - scope a tool
    /// the emulating host needs, such as the emulator itself, with <see cref="Emulators"/> instead.
    /// </remarks>
    public List<string> Processors { get; init; } = [];

    /// <summary>
    /// Emulators this tool is needed for: only a leg run under one of them needs it. Left out, every
    /// leg, emulated or not.
    /// </summary>
    public List<string> Emulators { get; init; } = [];

    /// <summary>How to install or update it, keyed by platform or <c>all</c>.</summary>
    public Dictionary<string, ToolInstall> Install { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What this tool is needed for, shown when it is missing.</summary>
    public string? Why { get; init; }
}

/// <summary>How to read a tool's version.</summary>
public sealed class ToolProbe
{
    /// <summary>Arguments that make the tool print its version.</summary>
    public List<string> Args { get; init; } = ["--version"];

    /// <summary>Regular expression whose first capturing group is the version.</summary>
    public string? Regex { get; init; }
}

/// <summary>How to install or update one tool on one platform.</summary>
public sealed class ToolInstall
{
    /// <summary>Package manager to use, such as <c>winget</c>, <c>apt</c> or <c>brew</c>.</summary>
    public string? Manager { get; init; }

    /// <summary>Package identifier within that manager.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// Explicit command, overriding <see cref="Manager"/>. The escape hatch for what
    /// no package manager expresses, such as an SDK installer or a git clone.
    /// </summary>
    public List<string>? Command { get; init; }
}
