using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// Deserializes every string-keyed dictionary in the configuration with a
/// case-insensitive comparer.
/// </summary>
/// <remarks>
/// Configuration keys are names a person typed: toolchains, legs, targets, named
/// commands. Looking up <c>Local-Debug</c> in a file that declares
/// <c>local-debug</c> must not report the leg as unknown.
/// <para>
/// A comparer supplied by a property initialiser does not survive deserialization:
/// the serializer constructs its own dictionary with the default ordinal comparer
/// and assigns it, so a configuration built in memory and the same configuration
/// read back from disk would disagree about whether a key exists. Populating the
/// existing instance instead would preserve the comparer, but it also changes
/// collections from replace to append, which would turn a toolchain declaring
/// <c>["windows"]</c> into one declaring <c>["all", "windows"]</c>. Converting only
/// dictionaries fixes the comparer and leaves list semantics alone.
/// </para>
/// </remarks>
public sealed class CaseInsensitiveDictionaryConverter : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && typeToConvert.GetGenericArguments()[0] == typeof(string);
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        var valueType = typeToConvert.GetGenericArguments()[1];

        return (JsonConverter)Activator.CreateInstance(
            typeof(CaseInsensitiveDictionaryConverter<>).MakeGenericType(valueType))!;
    }
}

/// <summary>Reads and writes one string-keyed dictionary, ignoring key case.</summary>
/// <typeparam name="TValue">The dictionary's value type.</typeparam>
public sealed class CaseInsensitiveDictionaryConverter<TValue> : JsonConverter<Dictionary<string, TValue>>
{
    /// <inheritdoc />
    public override Dictionary<string, TValue> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object, found {reader.TokenType}.");
        }

        // Values are read through their converter with this reader, not through
        // JsonSerializer.Deserialize, which reads a copy scoped to the value. A problem found
        // inside the value would then be reported at a line counted from the value's own
        // start instead of from the top of the file.
        var valueConverter = (JsonConverter<TValue>)options.GetConverter(typeof(TValue));
        var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return result;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected a property name, found {reader.TokenType}.");
            }

            var key = reader.GetString()
                ?? throw new JsonException("A dictionary key cannot be null.");

            if (!reader.Read())
            {
                throw new JsonException($"'{key}' has no value.");
            }

            // Two keys differing only in case would otherwise be one entry here and
            // two in the file, silently discarding whichever the author wrote first.
            if (result.ContainsKey(key))
            {
                throw new JsonException(
                    $"'{key}' is declared more than once; keys are compared ignoring case.");
            }

            // Refused for the reason NonNullListConverter gives: every value in these maps is
            // declared non-nullable, and the serializer does not enforce that for a value.
            var value = reader.TokenType == JsonTokenType.Null
                ? default
                : valueConverter.Read(ref reader, typeof(TValue), options);

            result[key] = value ?? throw new JsonException($"'{key}' is null; give it a value or remove it.");
        }

        throw new JsonException("Unexpected end of JSON while reading an object.");
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, TValue> value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        foreach (var (key, item) in value)
        {
            // Keys are the author's own names and are written exactly as given; only
            // property names follow the naming policy.
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, item, options);
        }

        writer.WriteEndObject();
    }
}
