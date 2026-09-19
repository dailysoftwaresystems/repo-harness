using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Core.Configuration;

/// <inheritdoc cref="IConfigStore"/>
public sealed class JsonConfigStore(IFileSystem fileSystem) : IConfigStore
{
    private static readonly JsonSerializerOptions Options = JsonConfigOptions.Default;

    private readonly IFileSystem _fileSystem = fileSystem;

    public HarnessConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_fileSystem.FileExists(path))
        {
            throw new ConfigException($"No configuration at '{path}'. Run '{ToolPackage.Command} init' first.");
        }

        var json = _fileSystem.ReadAllText(path);

        // Before the serializer, which would call a key this tool retired merely unknown: this
        // says what took its place.
        if (RetiredKeys.In(json) is [_, ..] retired)
        {
            throw new ConfigException($"'{path}' could not be read: {string.Join(" ", retired)}");
        }

        HarnessConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<HarnessConfig>(json, Options);
        }
        catch (JsonException ex)
        {
            // A missing required member and an unknown key are reported by the same
            // exception type as a stray brace. Saying "is not valid JSON" for those
            // sends the reader looking for a syntax error that is not there.
            throw new ConfigException($"'{path}' could not be read: {Describe(ex)}", ex);
        }

        if (config is null)
        {
            throw new ConfigException($"'{path}' is empty.");
        }

        HarnessConfigValidator.ThrowIfInvalid(config, path);
        return config;
    }

    public void Save(string path, HarnessConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(config);

        _fileSystem.WriteAllTextAtomic(path, Serialize(config));
    }

    public string Serialize(HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.Serialize(config, Options) + JsonConfigOptions.NewLine;
    }

    /// <summary>
    /// The parser's message, with the line it concerns. The serializer's own messages carry
    /// their location already; a message raised by one of the configuration's converters,
    /// such as a list holding null, does not, and is of little use without it.
    /// </summary>
    private static string Describe(JsonException exception)
    {
        if (exception.LineNumber is not { } line
            || exception.Message.Contains("LineNumber", StringComparison.Ordinal))
        {
            return exception.Message;
        }

        return $"{exception.Message} (line {line + 1})";
    }
}
