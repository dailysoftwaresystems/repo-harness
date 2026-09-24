using System.Net;
using System.Text.Json;

namespace RepoHarness.Core.Hosts;

/// <summary>Which versions of this tool are published where a host installs it from.</summary>
public interface IPublishedToolVersions
{
    /// <summary>
    /// The newest release published and listed that is newer than <paramref name="running"/>;
    /// <paramref name="running"/> itself where none is; or <see langword="null"/> where that cannot be told:
    /// no network, a feed that did not answer in time, or an answer that is not what it should be.
    /// </summary>
    /// <param name="running">The build running here, which a release must be newer than to be named.</param>
    /// <param name="cancellationToken">Stops the asking; cancelling it is reported, not read as no answer.</param>
    Task<SemanticVersion?> NewestAsync(SemanticVersion running, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPublishedToolVersions"/>
/// <remarks>
/// Asked of nuget.org, the one feed a host installs the tool from (<see cref="ToolPackage.Source"/>). Its flat
/// container answers in one request with every version the package id holds, and that is unlisted ones too, so
/// each release newer than the one running is then asked of its registration leaf whether it is listed, the
/// newest first, until one is. A release unlisted is one withdrawn: <c>dotnet tool update</c> never picks it,
/// so no dispatcher runs it, and named as newer it had every copy say it trailed a release nobody was on - until
/// a newer one was listed. A host running the newest release asks one question. The addresses are the ones the
/// feed's service index names for nuget.org, spelt out rather than looked up, which would be one more request
/// for an answer that is only ever advisory.
/// </remarks>
public sealed class NuGetPublishedToolVersions : IPublishedToolVersions, IDisposable
{
    /// <summary>Where nuget.org lists every version of this tool's package, listed or not.</summary>
    public static readonly Uri VersionsAddress =
        new($"https://api.nuget.org/v3-flatcontainer/{ToolPackage.Id.ToLowerInvariant()}/index.json");

    /// <summary>Where nuget.org keeps each version's registration leaf, which says whether it is listed.</summary>
    public static readonly Uri LeavesAddress =
        new($"https://api.nuget.org/v3/registration5-gz-semver2/{ToolPackage.Id.ToLowerInvariant()}/");

    /// <summary>
    /// Longest the answer is waited for, however many questions it takes. Short, because a command is
    /// waiting on it and the answer is only ever advice: a host with no route out waits this long once, and
    /// hears nothing.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Made when the feed is first asked, and not before: every command that loads a repository resolves
    /// this, and only one typed in a synced copy ever asks.
    /// </summary>
    private readonly Lazy<HttpClient> _client;

    /// <summary>Asks nuget.org over this machine's own network.</summary>
    public NuGetPublishedToolVersions()
        : this(handler: null)
    {
    }

    /// <summary>Asks through <paramref name="handler"/>, or over this machine's own network where none is given.</summary>
    /// <param name="handler">What sends the requests.</param>
    internal NuGetPublishedToolVersions(HttpMessageHandler? handler)
    {
        // The registration leaves are served compressed.
        _client = new(() => new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Budget,
        });
    }

    public async Task<SemanticVersion?> NewestAsync(SemanticVersion running, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(running);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        try
        {
            if (Releases(await ReadAsync(VersionsAddress, budget.Token).ConfigureAwait(false)) is not { } releases)
            {
                return null;
            }

            foreach (var release in releases.Where(release => SemanticVersion.Compare(release, running) > 0))
            {
                switch (IsListed(await ReadAsync(LeafAddress(release), budget.Token).ConfigureAwait(false)))
                {
                    case true:
                        return release;

                    case false:
                        continue;

                    default:
                        // Whether it is listed could not be told, and so neither can which release is newest.
                        return null;
                }
            }

            return running;
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

    /// <summary>The registration leaf of <paramref name="release"/>.</summary>
    /// <param name="release">A version of this tool's package.</param>
    public static Uri LeafAddress(SemanticVersion release)
    {
        ArgumentNullException.ThrowIfNull(release);

        return new Uri(LeavesAddress, $"{release.ToString().ToLowerInvariant()}.json");
    }

    /// <summary>
    /// Every release among the versions a flat container lists, the newest first, or <see langword="null"/>
    /// where <paramref name="json"/> lists none this build can read.
    /// </summary>
    /// <param name="json">The container's answer: <c>{"versions": ["0.5.9", "0.5.10", ...]}</c>.</param>
    /// <remarks>
    /// A prerelease is passed over. It sorts before the release it leads up to, and is never what a
    /// host is brought to unless its dispatcher runs one - in which case that dispatcher is ahead of
    /// every release, and so is the host it updates.
    /// </remarks>
    public static IReadOnlyList<SemanticVersion>? Releases(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var releases = new List<SemanticVersion>();

            foreach (var entry in versions.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && SemanticVersion.TryParse(entry.GetString(), out var version)
                    && !version.IsPrerelease)
                {
                    releases.Add(version);
                }
            }

            releases.Sort((left, right) => SemanticVersion.Compare(right, left));

            return releases.Count > 0 ? releases : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest release among the versions a flat container lists, or <see langword="null"/> where
    /// <paramref name="json"/> lists none this build can read.
    /// </summary>
    /// <param name="json">The container's answer.</param>
    public static SemanticVersion? NewestRelease(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return Releases(json) is [var newest, ..] ? newest : null;
    }

    /// <summary>
    /// Whether a registration leaf says its version is listed: listed where it says nothing, as the feed's
    /// own rule has it; <see langword="null"/> where <paramref name="json"/> is not a leaf this build can read.
    /// </summary>
    /// <param name="json">The leaf, or <see langword="null"/> where the feed gave none.</param>
    public static bool? IsListed(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("listed", out var listed))
            {
                return true;
            }

            return listed.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
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

    /// <summary>The body of what <paramref name="address"/> answers, or <see langword="null"/> where it answers with no success.</summary>
    private async Task<string?> ReadAsync(Uri address, CancellationToken cancellationToken)
    {
        using var response = await _client.Value.GetAsync(address, cancellationToken).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : null;
    }
}
