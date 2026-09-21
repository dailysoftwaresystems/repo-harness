using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Tools;

/// <summary>Which declared legs need a declared tool.</summary>
/// <remarks>
/// Every scope a tool declares must hold at once - its platforms, toolchains, legs, processors and
/// emulators - and each one left out holds for every leg. Asked per leg rather than per host, because
/// two legs on one host can need different tools: an MSVC leg and a MinGW leg share a Windows machine
/// and a platform, and only one of them starts <c>cl</c>.
/// </remarks>
public static class ToolScope
{
    /// <summary>Whether <paramref name="tool"/> is needed by the leg <paramref name="legName"/>.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <param name="config">The whole configuration, for the leg's toolchain and the leg sets.</param>
    /// <param name="legName">The leg's name, as the configuration declares it.</param>
    /// <param name="leg">The leg.</param>
    public static bool Covers(ToolConfig tool, HarnessConfig config, string legName, LegConfig leg)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(legName);
        ArgumentNullException.ThrowIfNull(leg);

        return PlatformScope.Applies(tool.Platforms, leg.Os)
            && Within(tool.Toolchains, VariantKey.For(config, leg, leg.Os).Toolchain)
            && Within(tool.Processors, leg.Processor)
            && Within(tool.Emulators, leg.Emulator)
            && (tool.Legs.Count == 0 || tool.Legs.Any(name => Names(config, name, legName)));
    }

    /// <summary>Whether a scope admits <paramref name="value"/>: left out it admits everything, and nothing admits no value.</summary>
    private static bool Within(IReadOnlyList<string> scope, string? value)
        => scope.Count == 0 || (value is { Length: > 0 } && scope.Contains(value, StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="name"/> is the leg, or a leg set holding it.</summary>
    private static bool Names(HarnessConfig config, string name, string legName)
        => string.Equals(name, legName, StringComparison.OrdinalIgnoreCase)
            || (config.LegSets.TryGetValue(name, out var set) && set.Contains(legName, StringComparer.OrdinalIgnoreCase));
}
