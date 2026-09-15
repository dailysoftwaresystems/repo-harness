using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// Serialisation settings for <c>config.json</c>. The file is meant to be read and
/// edited by people, so it is optimised for that rather than for compactness.
/// </summary>
public static class JsonConfigOptions
{
    /// <summary>
    /// Line ending of a written configuration, on every platform. The file is tracked, so
    /// one written with CRLF on Windows and LF elsewhere would differ by nothing but which
    /// machine happened to run init.
    /// </summary>
    public const string NewLine = "\n";

    /// <summary>The shared options instance.</summary>
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            NewLine = NewLine,

            // A property declared non-nullable is refused when the file sets it to null,
            // rather than loading and then failing as a crash in whatever reads it first.
            RespectNullableAnnotations = true,

            // config.json is hand edited, so tolerate the two things people reliably
            // write and strict parsers reject.
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,

            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // A misspelled key would otherwise be dropped without a word, reverting a
            // setting to its default while the file plainly appears to set it.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

            // Without this the default HTML-safe encoder writes "g++" as "g\u002B\u002B",
            // which is correct JSON and unreadable to the person maintaining the file.
            // Nothing here is ever emitted into a web page.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { SkipEmptyCollections },
            },
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new CaseInsensitiveDictionaryConverter());
        options.Converters.Add(new NonNullListConverter());
        return options;
    }

    /// <summary>
    /// Omits properties holding empty collections. A generated file full of
    /// <c>"env": {}</c> buries the settings that actually matter.
    /// </summary>
    private static void SkipEmptyCollections(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType)
                || property.PropertyType == typeof(string))
            {
                continue;
            }

            property.ShouldSerialize = (_, value) => value switch
            {
                null => false,
                ICollection collection => collection.Count > 0,
                // Any() disposes the enumerator it takes; a bare MoveNext() does not.
                IEnumerable enumerable => enumerable.Cast<object?>().Any(),
                _ => true,
            };
        }
    }
}
