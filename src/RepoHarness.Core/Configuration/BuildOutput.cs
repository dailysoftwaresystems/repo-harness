using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// One file a build must produce for the build to be witnessed, named per platform where the
/// platforms disagree.
/// </summary>
/// <remarks>
/// <para>
/// The same target is not the same file everywhere: a program CMake calls <c>dsscp</c> is
/// <c>dsscp.exe</c> on Windows, and a static library differs by prefix as well as suffix
/// (<c>libfoo.a</c> against <c>foo.lib</c>). A single path is therefore wrong on one platform or
/// the other, which left a mixed leg set with no way to witness a build at all.
/// </para>
/// <para>
/// Written as a bare string it applies everywhere, which is what every existing configuration
/// means and why none of them has to change. Written as a map it is keyed by platform, or by
/// <c>all</c> for the general case, the same shape a tool's <c>install</c> already uses.
/// </para>
/// <para>
/// Adding a suffix automatically was the alternative and is not enough: it would have to guess
/// which entries name programs, and it cannot express a name that differs by more than its suffix.
/// </para>
/// </remarks>
[JsonConverter(typeof(BuildOutputConverter))]
public sealed class BuildOutput : IEquatable<BuildOutput>
{
    /// <summary>The paths this entry declares, keyed by platform or <see cref="PlatformScope.Every"/>.</summary>
    public Dictionary<string, string> Paths { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the entry was written as a bare string rather than a map.</summary>
    /// <remarks>
    /// Kept so the file can be written back as it was read. A path that applies everywhere and was
    /// spelled as a string would otherwise come back as a map, turning every save into a diff
    /// against a file nobody edited.
    /// </remarks>
    public bool IsPlain { get; init; }

    /// <summary>An entry naming one path on every platform.</summary>
    /// <param name="path">The path, relative to the build directory.</param>
    public static BuildOutput Everywhere(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return new BuildOutput
        {
            Paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PlatformScope.Every] = path,
            },
            IsPlain = true,
        };
    }

    /// <summary>A bare path, which applies on every platform.</summary>
    /// <remarks>
    /// A string is a legal entry in the file, so it is a legal entry in code: a configuration built
    /// in memory should read the same way as the one it round-trips to.
    /// </remarks>
    /// <param name="path">The path, relative to the build directory.</param>
    public static implicit operator BuildOutput(string path) => Everywhere(path);

    /// <summary>
    /// The path this entry names on <paramref name="platformKey"/>, or <see langword="null"/> when
    /// it names none.
    /// </summary>
    /// <param name="platformKey">The platform the build ran on.</param>
    public string? For(string? platformKey) => PlatformScope.Select(Paths, platformKey);

    /// <summary>Whether this entry names a path on <paramref name="platformKey"/>.</summary>
    /// <param name="platformKey">The platform the build ran on.</param>
    public bool Covers(string? platformKey) => PlatformScope.Covers(Paths, platformKey);

    /// <summary>
    /// Whether <paramref name="other"/> names the same paths for the same platforms.
    /// </summary>
    /// <remarks>
    /// Compared by what it declares rather than by reference, so a configuration saved and read
    /// back equals the one it came from. Platform names compare ignoring case, as they do
    /// everywhere else; the paths do not, because a file system may or may not, and a claim that
    /// two differently-spelled paths are one file is not this type's to make.
    /// </remarks>
    /// <param name="other">The entry to compare with.</param>
    public bool Equals(BuildOutput? other)
        => other is not null
            && Paths.Count == other.Paths.Count
            && Paths.All(entry
                => other.Paths.TryGetValue(entry.Key, out var path)
                && string.Equals(entry.Value, path, StringComparison.Ordinal));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as BuildOutput);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Order-independent, because two maps declaring the same platforms in a different order are
        // the same entry and a dictionary does not promise an order to begin with.
        var hash = Paths.Count;

        foreach (var (platform, path) in Paths)
        {
            hash ^= HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(platform),
                StringComparer.Ordinal.GetHashCode(path));
        }

        return hash;
    }

    /// <summary>The entry as a refusal shows it: the bare path, or every platform it names.</summary>
    public override string ToString()
        => IsPlain && Paths.TryGetValue(PlatformScope.Every, out var plain)
            ? plain
            : string.Join(", ", Paths.Select(entry => $"{entry.Key}: {entry.Value}"));
}

/// <summary>
/// Reads a <see cref="BuildOutput"/> written either as a bare string or as a map of platform to
/// path, and writes it back in the shape it was read.
/// </summary>
/// <remarks>
/// Read with the caller's reader rather than through <c>JsonSerializer.Deserialize</c>, for the
/// reason <see cref="NonNullListConverter"/> and <see cref="CaseInsensitiveDictionaryConverter"/>
/// give: a copy scoped to the value reports a problem at a line counted from the value's own start
/// instead of from the top of the file.
/// </remarks>
public sealed class BuildOutputConverter : JsonConverter<BuildOutput>
{
    /// <inheritdoc />
    public override BuildOutput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return BuildOutput.Everywhere(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"A buildOutputs entry is a path, or a mapping of platform to path, found {reader.TokenType}.");
        }

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new BuildOutput { Paths = paths };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected a platform name, found {reader.TokenType}.");
            }

            var platform = reader.GetString() ?? string.Empty;

            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(
                    $"A buildOutputs entry for '{platform}' is not a path.");
            }

            // Refused rather than resolved one way: two spellings of one platform are one key on
            // this dictionary, so the second would silently replace the first.
            if (!paths.TryAdd(platform, reader.GetString() ?? string.Empty))
            {
                throw new JsonException(
                    $"A buildOutputs entry names platform '{platform}' twice; nothing could say which path it meant.");
            }
        }

        throw new JsonException("Unexpected end of JSON while reading a buildOutputs entry.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BuildOutput value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.IsPlain && value.Paths.TryGetValue(PlatformScope.Every, out var plain))
        {
            writer.WriteStringValue(plain);
            return;
        }

        writer.WriteStartObject();

        foreach (var (platform, path) in value.Paths)
        {
            writer.WriteString(platform, path);
        }

        writer.WriteEndObject();
    }
}
