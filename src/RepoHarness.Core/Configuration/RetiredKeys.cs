using System.Text.Json;

namespace RepoHarness.Core.Configuration;

/// <summary>Keys this tool once read and no longer does, and where what each meant is declared now.</summary>
/// <remarks>
/// A retired key is refused like any unknown one, because a key that loads is a key that is read.
/// The refusal says what took its place: told only that the key is unknown, a reader deletes the
/// line and loses what it was there for.
/// <para>
/// Looked for in the file itself, before the serializer reads it. The serializer does refuse the
/// key, but inside a map of named entries - the hosts under <c>wsl</c> and <c>ssh</c> among them -
/// it no longer knows which entry it was reading, and could not say whose key it was.
/// </para>
/// </remarks>
internal static class RetiredKeys
{
    private const string CompilerCacheDirectory = "compilerCacheDirectory";

    /// <summary>
    /// Each retired key <paramref name="json"/> holds, said with what took its place: empty when it
    /// holds none, or is not JSON at all, which the serializer then reports as it reports any file it
    /// cannot read.
    /// </summary>
    /// <param name="json">The configuration file's text.</param>
    public static IReadOnlyList<string> In(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;

        try
        {
            // Read as the serializer reads it, so a file it accepts is one this can look through.
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            return [.. HostSections(document.RootElement)
                .Where(section => Property(section.Settings, CompilerCacheDirectory) is not null)
                .Select(section => $"{section.Name} {CompilerCacheDirectory} is no longer read. A compiler "
                    + "cache's store is that cache's own variable: declare it under the host's env - "
                    + "\"env\": { \"CCACHE_DIR\": \"...\" } for ccache - where every process a leg "
                    + "starts on that host sees it, and a build keys the cache against the leg's own tree.")];
        }
    }

    /// <summary>Every host section the file declares, named as a refusal names it.</summary>
    private static IEnumerable<(string Name, JsonElement Settings)> HostSections(JsonElement root)
    {
        if (Property(root, "hosts") is not { ValueKind: JsonValueKind.Object } hosts)
        {
            yield break;
        }

        if (Property(hosts, "local") is { ValueKind: JsonValueKind.Object } local)
        {
            yield return ("hosts.local", local);
        }

        foreach (var kind in new[] { "wsl", "ssh" })
        {
            if (Property(hosts, kind) is not { ValueKind: JsonValueKind.Object } declared)
            {
                continue;
            }

            foreach (var host in declared.EnumerateObject().Where(host => host.Value.ValueKind == JsonValueKind.Object))
            {
                yield return ($"hosts.{kind} '{host.Name}'", host.Value);
            }
        }
    }

    /// <summary>The property <paramref name="name"/> names, found ignoring case as the serializer finds it.</summary>
    private static JsonElement? Property(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject()
                .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(property => (JsonElement?)property.Value)
                .FirstOrDefault()
            : null;
}
