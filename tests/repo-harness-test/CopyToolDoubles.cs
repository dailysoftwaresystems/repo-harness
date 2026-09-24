using RepoHarness.Core.Hosts;

namespace RepoHarness.Tests;

/// <summary>
/// A feed that answers with the version it is set to, or cannot be reached where it is set to none, and
/// counts how often it was asked. No test reaches nuget.org.
/// </summary>
public sealed class PublishedVersionsDouble(string? newest = null) : IPublishedToolVersions
{
    /// <summary>The newest release the feed lists, or <see langword="null"/> for a feed that cannot be reached.</summary>
    public string? Newest { get; set; } = newest;

    /// <summary>How many times the feed was asked.</summary>
    public int Asked { get; private set; }

    public Task<SemanticVersion?> NewestAsync(SemanticVersion running, CancellationToken cancellationToken = default)
    {
        Asked++;
        return Task.FromResult(SemanticVersion.TryParse(Newest, out var version) ? version : null);
    }
}

/// <summary>A running build whose version a test sets.</summary>
public sealed class RunningToolDouble(string version = "1.0.0") : IToolIdentityProvider
{
    /// <summary>The version the running build reports.</summary>
    public string Version { get; set; } = version;

    public ToolIdentity Current => new(Version, "0000");
}
