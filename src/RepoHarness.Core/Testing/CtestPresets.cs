using System.Text.Json;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Testing;

/// <summary>
/// Which of its filters a ctest test preset sets, read from the preset files ctest reads it from.
/// </summary>
/// <remarks>
/// Only whether each filter is set to something, never what it matches: a value may hold macros only
/// ctest expands, and whether a preset sets a filter at all is what decides whether an option given on
/// ctest's command line is read beside it. The one value read is a flag's: whether the preset takes a
/// union. Read as ctest reads presets: <c>CMakePresets.json</c> and <c>CMakeUserPresets.json</c> in the
/// directory ctest starts in, each with the files its <c>include</c> list names, relative to it; and
/// every preset a preset inherits, however far along. A filter is set where any of them sets it to
/// something: measured with ctest 4.3.2, it takes the first value along the inherits that says
/// something, and an empty one says nothing - a preset that sets a filter to "" still has the one it
/// inherits. Whether a preset takes the union of what it chooses is decided by the first to say, itself
/// before the ones it inherits, in order.
/// </remarks>
internal static class CtestPresets
{
    /// <summary>The preset files ctest reads, in the directory it starts in.</summary>
    private static readonly string[] Files = ["CMakePresets.json", "CMakeUserPresets.json"];

    /// <summary>
    /// What a preset sets when it takes the union of the tests its filters choose rather than those
    /// every one chooses; named among the filters it sets.
    /// </summary>
    public const string Union = "include.useUnion";

    /// <summary>
    /// The filters test preset <paramref name="name"/> sets to something - <c>include.name</c>,
    /// <c>exclude.label</c> and the like, and <see cref="Union"/> where it takes a union - as ctest would read
    /// it starting in <paramref name="directory"/>; or why that could not be read, to end a sentence.
    /// </summary>
    /// <param name="fileSystem">What the preset files are read through.</param>
    /// <param name="directory">The directory ctest starts in.</param>
    /// <param name="name">The test preset.</param>
    public static (IReadOnlySet<string>? Filters, string? Unread) FiltersOf(IFileSystem fileSystem, string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var presets = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        try
        {
            var read = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in Files.Select(file => Path.GetFullPath(Path.Combine(directory, file))).Where(fileSystem.FileExists))
            {
                Load(fileSystem, file, presets, read);
            }

            if (!presets.ContainsKey(name))
            {
                return (null, $"no test preset '{name}' is in the preset files in '{directory}'");
            }

            var filters = new HashSet<string>(StringComparer.Ordinal);

            foreach (var filter in new[] { "include.name", "include.label", "exclude.name", "exclude.label" })
            {
                if (Sets(presets, name, filter, []))
                {
                    filters.Add(filter);
                }
            }

            if (Decides(presets, name, Union, []) is { ValueKind: JsonValueKind.True })
            {
                filters.Add(Union);
            }

            return (filters, null);
        }
        // An include naming a path the runtime rejects - a NUL in it - is as unreadable as one that is not there.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (null, $"the preset files in '{directory}' could not be read: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>Reads the test presets in <paramref name="file"/>, and in each file it includes, into <paramref name="presets"/>.</summary>
    private static void Load(IFileSystem fileSystem, string file, Dictionary<string, JsonElement> presets, HashSet<string> read)
    {
        if (!read.Add(file))
        {
            return;
        }

        using var document = JsonDocument.Parse(fileSystem.ReadAllText(file));
        var root = document.RootElement;

        if (root.TryGetProperty("include", out var include))
        {
            foreach (var entry in include.EnumerateArray())
            {
                var path = entry.GetString() ?? throw new InvalidOperationException($"'{file}' includes a file it does not name");

                // Expanded by CMake alone, from values this build cannot see.
                if (path.Contains('$', StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"'{file}' includes '{path}', whose macros only CMake expands");
                }

                Load(fileSystem, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, path)), presets, read);
            }
        }

        if (root.TryGetProperty("testPresets", out var tests))
        {
            foreach (var preset in tests.EnumerateArray())
            {
                presets[preset.GetProperty("name").GetString() ?? throw new InvalidOperationException($"'{file}' holds a test preset with no name")] = preset.Clone();
            }
        }
    }

    /// <summary>
    /// Whether preset <paramref name="name"/>, or any it inherits however far along, sets <paramref name="filter"/>
    /// to something: an empty value says nothing, and leaves the one it inherits in force.
    /// </summary>
    private static bool Sets(Dictionary<string, JsonElement> presets, string name, string filter, HashSet<string> along)
    {
        var preset = Preset(presets, name, along);

        return Own(preset, filter) is { } value && (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 0)
            || Parents(preset, name).Any(parent => Sets(presets, parent, filter, [.. along]));
    }

    /// <summary>
    /// The value the first to say gives <paramref name="setting"/>: preset <paramref name="name"/> itself, or else
    /// the presets it inherits in order, each asked the same way; <see langword="null"/> where none says.
    /// </summary>
    private static JsonElement? Decides(Dictionary<string, JsonElement> presets, string name, string setting, HashSet<string> along)
    {
        var preset = Preset(presets, name, along);

        return Own(preset, setting) ?? Parents(preset, name)
            .Select(parent => Decides(presets, parent, setting, [.. along]))
            .FirstOrDefault(value => value is not null);
    }

    /// <summary>Preset <paramref name="name"/>, refused where the presets along the way to it inherit it again, or never declare it.</summary>
    private static JsonElement Preset(Dictionary<string, JsonElement> presets, string name, HashSet<string> along)
        => !along.Add(name)
            ? throw new InvalidOperationException($"test preset '{name}' inherits itself")
            : presets.TryGetValue(name, out var preset)
                ? preset
                : throw new InvalidOperationException($"test preset '{name}' is inherited and not declared");

    /// <summary>What <paramref name="preset"/> itself gives <paramref name="setting"/>, under <c>filter</c>, or <see langword="null"/>.</summary>
    private static JsonElement? Own(JsonElement preset, string setting)
    {
        var dot = setting.IndexOf('.', StringComparison.Ordinal);

        return preset.TryGetProperty("filter", out var filters)
            && filters.TryGetProperty(setting[..dot], out var ofKind)
            && ofKind.TryGetProperty(setting[(dot + 1)..], out var value)
                ? value
                : null;
    }

    /// <summary>The presets <paramref name="preset"/> inherits, in order.</summary>
    private static IEnumerable<string> Parents(JsonElement preset, string name)
        => !preset.TryGetProperty("inherits", out var inherits)
            ? []
            : inherits.ValueKind == JsonValueKind.String
                ? [inherits.GetString()!]
                : [.. inherits.EnumerateArray().Select(parent => parent.GetString() ?? throw new InvalidOperationException($"test preset '{name}' inherits a preset it does not name"))];
}
