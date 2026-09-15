using System.Reflection;
using System.Security.Cryptography;

namespace RepoHarness.Core.Hosts;

/// <summary>The package this tool is published as, which is what a host installs to run it.</summary>
public static class ToolPackage
{
    /// <summary>The NuGet package ID, as the CLI project declares it.</summary>
    public const string Id = "DssHarness";

    /// <summary>The command the package installs, as the CLI project declares it.</summary>
    public const string Command = "DssHarness";

    /// <summary>The oldest .NET SDK major version that can install and run the tool.</summary>
    public const int MinimumSdkMajor = 10;

    /// <summary>
    /// The one feed a host installs the tool from. Named on the command line, it replaces every feed the
    /// host has configured, so none of those can supply a different package under the same name.
    /// </summary>
    public const string Source = "https://api.nuget.org/v3/index.json";

    /// <summary>
    /// Where a global install of the tool is, from the home directory commands start in, with the extension
    /// the host's operating system needs and the separator its shell needs.
    /// </summary>
    public static string PathFromHome(bool windowsHost, RemoteShell shell)
    {
        var program = windowsHost ? Command + ".exe" : Command;

        return shell == RemoteShell.Cmd ? $@".dotnet\tools\{program}" : $".dotnet/tools/{program}";
    }
}

/// <summary>Which build of DssHarness a process is.</summary>
/// <param name="Version">The version it reports with <c>--version</c>.</param>
/// <param name="AssemblySha256">
/// The SHA-256 of its main assembly, in lower-case hex. The version alone cannot tell a build from
/// source apart from the published package of the same number, and the assembly installed from one
/// package is the same bytes on every operating system.
/// </param>
public sealed record ToolIdentity(string Version, string AssemblySha256);

/// <summary>Identifies the build of DssHarness that is running.</summary>
public interface IToolIdentityProvider
{
    /// <summary>The running build.</summary>
    ToolIdentity Current { get; }
}

/// <inheritdoc cref="IToolIdentityProvider"/>
/// <remarks>Read from the assembly the process started with, which is the tool's own assembly.</remarks>
public sealed class EntryAssemblyToolIdentityProvider : IToolIdentityProvider
{
    private readonly Lazy<ToolIdentity> _current = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    public ToolIdentity Current => _current.Value;

    private static ToolIdentity Read()
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("The process has no entry assembly to identify.");

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException($"'{assembly.Location}' declares no version.");

        using var stream = File.OpenRead(assembly.Location);

        // Anything after '+' names the commit a build came from; the version a package is published
        // under never carries it.
        return new ToolIdentity(informational.Split('+', 2)[0], Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
}
