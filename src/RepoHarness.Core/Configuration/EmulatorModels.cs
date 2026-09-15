namespace RepoHarness.Core.Configuration;

/// <summary>
/// A way to run programs built for another processor on a host without leaving the host's
/// operating system: qemu's user mode on Linux, Rosetta on macOS, Prism on Windows.
/// </summary>
/// <remarks>
/// A whole virtual machine is not an emulator in this sense. It runs an operating system of its
/// own, and is declared as the ssh host it is.
/// </remarks>
public sealed class EmulatorConfig
{
    /// <summary>Operating system of the hosts this emulator runs on: <c>windows</c>, <c>linux</c> or <c>macos</c>.</summary>
    public required string HostOs { get; init; }

    /// <summary>Processor of the hosts this emulator runs on, such as <c>x86_64</c>.</summary>
    public required string HostProcessor { get; init; }

    /// <summary>Processor the programs it runs are built for, such as <c>arm64</c>.</summary>
    public required string Processor { get; init; }

    /// <summary>
    /// Command placed in front of each program, one element per argument, such as
    /// <c>["qemu-aarch64", "-L", "/usr/aarch64-linux-gnu"]</c> or <c>["arch", "-x86_64"]</c>. Left out
    /// when the operating system runs such programs by itself, as Windows does through Prism and
    /// Linux does through a registered binfmt handler.
    /// </summary>
    public List<string>? Launcher { get; init; }

    /// <summary>Environment the emulator needs, such as <c>QEMU_LD_PREFIX</c>.</summary>
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What must be present on the host: a program name, looked up on the PATH, or a path to a
    /// file or directory, such as a sysroot. A missing one makes every leg that needs the emulator
    /// on that host unavailable, by name, before anything starts.
    /// </summary>
    public List<string> Requires { get; init; } = [];

    /// <summary>
    /// The phases of a leg that run through the emulator: <c>test</c>, <c>build</c>, or both. Tests
    /// only, by default, because the usual way to build for another processor is a native
    /// cross-build.
    /// </summary>
    public List<string> Phases { get; init; } = ["test"];

    /// <summary>A program run through the emulator to prove it works, before any leg relies on it.</summary>
    public required EmulatorWitness Witness { get; init; }

    /// <summary>The entry under <c>tools</c> that installs this emulator.</summary>
    public string? Tool { get; init; }

    /// <summary>What this emulator is for, shown in reports.</summary>
    public string? Description { get; init; }
}

/// <summary>Evidence that an emulator really runs programs for its processor.</summary>
/// <remarks>
/// Without it, an emulator that ran nothing, or a program the host ran natively, would count as
/// coverage of a processor that was never exercised. The command should print something only a
/// program for the emulated processor can, such as <c>uname -m</c> from that processor's userland.
/// </remarks>
public sealed class EmulatorWitness
{
    /// <summary>Program and arguments, run after the launcher.</summary>
    public required List<string> Command { get; init; }

    /// <summary>Pattern the program's output must match, such as <c>^aarch64$</c>. Matched line by line.</summary>
    public required string Pattern { get; init; }
}
