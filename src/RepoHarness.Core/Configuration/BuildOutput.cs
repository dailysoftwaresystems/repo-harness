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
    private readonly Dictionary<string, string> _paths;

    private BuildOutput(Dictionary<string, string> paths, string? plain)
    {
        _paths = paths;
        Plain = plain;
    }

    /// <summary>
    /// The bare path this entry was written as, or <see langword="null"/> when it was written as a
    /// map of platform to path.
    /// </summary>
    /// <remarks>
    /// The path itself rather than a flag saying there is one, so the shape cannot disagree with
    /// the content. Held so the file is written back as it was read: a path applying everywhere and
    /// spelled as a string would otherwise return as a map, turning every save into a diff against
    /// a file nobody edited. A reader asking "does this cover every platform" asks this, and gets
    /// an answer the type guarantees rather than one two fields have to keep agreeing on.
    /// </remarks>
    public string? Plain { get; }

    /// <summary>The paths this entry declares, keyed by platform or <see cref="PlatformScope.Every"/>.</summary>
    public IReadOnlyDictionary<string, string> Paths => _paths;

    /// <summary>An entry naming one path on every platform.</summary>
    /// <param name="path">The path, relative to the build directory.</param>
    /// <exception cref="ArgumentException">The path is blank; an entry naming nothing witnesses nothing.</exception>
    public static BuildOutput Everywhere(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new BuildOutput(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [PlatformScope.Every] = path },
            path);
    }

    /// <summary>An entry naming one path per platform.</summary>
    /// <remarks>
    /// The comparer is fixed here rather than taken from the caller, so every entry answers a
    /// platform lookup the same way however it was built, and two entries declaring the same
    /// platforms compare equal whichever of them is asked first.
    /// </remarks>
    /// <param name="paths">The paths, keyed by platform name or <see cref="PlatformScope.Every"/>.</param>
    public static BuildOutput Keyed(IEnumerable<KeyValuePair<string, string>> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new BuildOutput(new Dictionary<string, string>(paths, StringComparer.OrdinalIgnoreCase), plain: null);
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
            && _paths.Count == other._paths.Count
            && _paths.All(entry
                => other._paths.TryGetValue(entry.Key, out var path)
                && string.Equals(entry.Value, path, StringComparison.Ordinal));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as BuildOutput);

    /// <summary>Whether two entries declare the same paths for the same platforms.</summary>
    /// <remarks>
    /// Defined because the type has value equality: left out, <c>==</c> would answer by reference
    /// while <see cref="Equals(BuildOutput)"/> answered by content, and the two would disagree
    /// silently at whichever call site reached for the operator.
    /// </remarks>
    /// <param name="left">One entry.</param>
    /// <param name="right">The other.</param>
    public static bool operator ==(BuildOutput? left, BuildOutput? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Whether two entries declare different paths.</summary>
    /// <param name="left">One entry.</param>
    /// <param name="right">The other.</param>
    public static bool operator !=(BuildOutput? left, BuildOutput? right) => !(left == right);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Order-independent, because two maps declaring the same platforms in a different order are
        // the same entry and a dictionary does not promise an order to begin with.
        var hash = _paths.Count;

        foreach (var (platform, path) in _paths)
        {
            hash ^= HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(platform),
                StringComparer.Ordinal.GetHashCode(path));
        }

        return hash;
    }

    /// <summary>The entry as a refusal shows it: the bare path, or every platform it names.</summary>
    public override string ToString()
        => Plain ?? string.Join(", ", _paths.Select(entry => $"{entry.Key}: {entry.Value}"));
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
            // Refused here rather than left to resolve to the build directory itself, which is never
            // found there and would fail a build that produced everything it actually named.
            return reader.GetString() is { } plain && !string.IsNullOrWhiteSpace(plain)
                ? BuildOutput.Everywhere(plain)
                : throw new JsonException("A buildOutputs entry is an empty path, which names no file.");
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
                return paths.Count > 0
                    ? BuildOutput.Keyed(paths)
                    : throw new JsonException(
                        "A buildOutputs entry names no path at all; an entry that witnesses nothing "
                        + "is the same as not declaring it, which is refused for the same reason.");
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

            var path = reader.GetString();

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new JsonException($"A buildOutputs entry for '{platform}' is an empty path, which names no file.");
            }

            // Refused rather than resolved one way: two spellings of one platform are one key on
            // this dictionary, so the second would silently replace the first.
            if (!paths.TryAdd(platform, path))
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

        if (value.Plain is { } plain)
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
