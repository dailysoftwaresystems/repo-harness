using System.Text.Json;

namespace RepoHarness.Core.Hosts;

/// <summary>Which versions of this tool are published where a host installs it from.</summary>
public interface IPublishedToolVersions
{
    /// <summary>
    /// The newest release published, or <see langword="null"/> where that cannot be told: no network, a
    /// feed that did not answer in time, or an answer that is not the list it should be.
    /// </summary>
    /// <param name="cancellationToken">Stops the asking; cancelling it is reported, not read as no answer.</param>
    Task<SemanticVersion?> NewestAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPublishedToolVersions"/>
/// <remarks>
/// Asked of nuget.org, the one feed a host installs the tool from (<see cref="ToolPackage.Source"/>), through
/// its flat container: one request, answered with every version the package id holds. The address is the
/// container that feed's service index names for nuget.org, spelt out rather than looked up, which would
/// be a second request for an answer that is only ever advisory.
/// </remarks>
public sealed class NuGetPublishedToolVersions : IPublishedToolVersions, IDisposable
{
    /// <summary>Where nuget.org lists every version of this tool's package.</summary>
    public static readonly Uri VersionsAddress =
        new($"https://api.nuget.org/v3-flatcontainer/{ToolPackage.Id.ToLowerInvariant()}/index.json");

    /// <summary>
    /// Longest the answer is waited for. Short, because a command is waiting on it and the answer is
    /// only ever advice: a host with no route out waits this long once, and hears nothing.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Made when the feed is first asked, and not before: every command that loads a repository resolves
    /// this, and only one typed in a synced copy ever asks.
    /// </summary>
    private readonly Lazy<HttpClient> _client = new(() => new HttpClient { Timeout = Budget });

    public async Task<SemanticVersion?> NewestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.Value.GetAsync(VersionsAddress, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return NewestRelease(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget ran out, which HttpClient reports as a cancellation of its own.
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest release among the versions a flat container lists, or <see langword="null"/> where
    /// <paramref name="json"/> lists none this build can read.
    /// </summary>
    /// <param name="json">The container's answer: <c>{"versions": ["0.5.9", "0.5.10", ...]}</c>.</param>
    /// <remarks>
    /// A prerelease is passed over. It sorts before the release it leads up to, and is never what a
    /// host is brought to unless its dispatcher runs one - in which case that dispatcher is ahead of
    /// every release, and so is the host it updates.
    /// </remarks>
    public static SemanticVersion? NewestRelease(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            SemanticVersion? newest = null;

            foreach (var entry in versions.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && SemanticVersion.TryParse(entry.GetString(), out var version)
                    && !version.IsPrerelease
                    && (newest is null || SemanticVersion.Compare(version, newest) > 0))
                {
                    newest = version;
                }
            }

            return newest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
