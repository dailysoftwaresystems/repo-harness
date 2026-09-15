using System.Text;
using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Anchors;

/// <summary>Which of the two registries a file is.</summary>
public enum AnchorRegistryKind
{
    /// <summary>The live anchors: open, gated and disclosed.</summary>
    Pending,

    /// <summary>The archive of closed anchors.</summary>
    Done,
}

/// <summary>The text of a new, empty registry: an introduction and an anchor table with no rows.</summary>
/// <remarks>
/// Embedded in the assembly rather than shipped beside it, so <c>init</c> can never find it missing
/// and it always matches the tool that reads it.
/// </remarks>
public static class AnchorRegistrySkeleton
{
    private const string ResourceName = "RepoHarness.Core.Anchors.Templates.anchor-registry.md";

    /// <summary>The skeleton for <paramref name="kind"/>, with its id examples in the configured spelling.</summary>
    public static string Render(AnchorRegistryKind kind, AnchorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var (title, purpose) = kind switch
        {
            AnchorRegistryKind.Pending => (
                "Deferred-Anchor Registry",
                "The live anchors: every piece of deferred work that is still open, gated or disclosed. "
                + "The table below is the work that is left."),
            _ => (
                "Deferred-Anchor Registry — Done",
                "The archive of closed anchors, each moved here from the pending registry when it closed. "
                + "Nothing in this file is work, and no anchor is ever added here directly."),
        };

        return ReadTemplate()
            .Replace("{{title}}", title, StringComparison.Ordinal)
            .Replace("{{purpose}}", purpose, StringComparison.Ordinal)
            .Replace("{{example}}", AnchorIdRules.From(settings).Example(), StringComparison.Ordinal);
    }

    private static string ReadTemplate()
    {
        using var stream = typeof(AnchorRegistrySkeleton).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing from this build.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Line feeds whatever the checkout did to the resource: the registry is tracked, and its
        // bytes must not depend on the machine that built the tool.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
