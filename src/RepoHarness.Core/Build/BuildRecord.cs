using System.Globalization;
using System.Text;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Build;

/// <summary>
/// What the harness keeps in a variant's build directory about the build that last ran there: what the
/// build system was given, whether anything doubted the build, and, once it finished, the newest file it
/// left.
/// </summary>
/// <param name="Unordered">
/// Why nothing the build stamped can be ordered, which the next build gives as its reason for starting
/// from clean; <see langword="null"/> where nothing doubted it.
/// </param>
/// <param name="Variant">The variant's directory name, for whoever reads the file.</param>
/// <param name="Newest">
/// The newest file the build left in the directory, relative to it, and when it was written: read as the
/// build finished, and <see langword="null"/> for a build that never finished, or whose directory could
/// not then be read.
/// </param>
/// <param name="Contents">The content hash of every input the build system was given, by path.</param>
/// <param name="Written">When each input had last been written as the build began, by path.</param>
/// <remarks>
/// Written as lines an earlier version still reads the parts of that it knew. The first line is the mark,
/// <c>clock-stepped</c> for an unordered build as it always was - 0.5.8 looks for that word anywhere in
/// the file - with the reason on a line of its own; each input's hash is a line of its own, as before; and
/// what an earlier version never wrote, it passes over.
/// </remarks>
internal sealed record BuildRecord(
    string? Unordered,
    string Variant,
    WrittenFile? Newest,
    IReadOnlyDictionary<string, string> Contents,
    IReadOnlyDictionary<string, DateTime> Written)
{
    /// <summary>The record's name in a build directory.</summary>
    public const string FileName = ".harness-build";

    /// <summary>
    /// Why a record an earlier version marked unordered starts the next build from clean: it never said
    /// what made it so.
    /// </summary>
    public const string Unexplained = "an unordered build: an earlier version recorded that nothing the previous build "
        + "stamped can be ordered, without saying why";

    /// <summary>What the first line says when nothing doubted the build.</summary>
    private const string OrderedMark = "clean";

    /// <summary>What the first line says when nothing the build stamped can be ordered.</summary>
    private const string UnorderedMark = "clock-stepped";

    /// <summary>What the line saying why a build is unordered starts with.</summary>
    private const string WhyPrefix = "why ";

    /// <summary>What the line naming the newest file the build left starts with.</summary>
    private const string NewestPrefix = "newest ";

    /// <summary>What every line holding an input's content hash starts with.</summary>
    private const string ContentPrefix = "in ";

    /// <summary>What every line holding when an input had last been written starts with.</summary>
    private const string WrittenPrefix = "at ";

    /// <summary>The record as the file holds it.</summary>
    public string Write()
    {
        var text = new StringBuilder()
            .Append(Unordered is null ? OrderedMark : UnorderedMark).Append('\n');

        if (Unordered is not null)
        {
            text.Append(WhyPrefix).Append(Unordered.ReplaceLineEndings(" ")).Append('\n');
        }

        text.Append(Variant).Append('\n');

        if (Newest is not null)
        {
            text.Append(NewestPrefix).Append(Ticks(Newest.LastWriteTimeUtc)).Append(' ').Append(Newest.Path).Append('\n');
        }

        foreach (var (path, content) in Contents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(ContentPrefix).Append(content).Append(' ').Append(path).Append('\n');
        }

        foreach (var (path, written) in Written.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(WrittenPrefix).Append(Ticks(written)).Append(' ').Append(path).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The record <paramref name="text"/> holds, in this version's words or an earlier one's.</summary>
    /// <param name="text">What the file holds.</param>
    public static BuildRecord Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var next = 1;
        string? why = null;

        if (next < lines.Count && lines[next].StartsWith(WhyPrefix, StringComparison.Ordinal))
        {
            why = lines[next][WhyPrefix.Length..];
            next++;
        }

        var variant = next < lines.Count ? lines[next] : string.Empty;
        WrittenFile? newest = null;
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        var written = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var line in lines.Skip(next + 1))
        {
            if (Split(line, NewestPrefix) is (var newestTicks, var newestPath) && Time(newestTicks) is { } newestTime)
            {
                newest = new WrittenFile(newestPath, newestTime);
            }
            else if (Split(line, ContentPrefix) is (var content, var path))
            {
                contents[path] = content;
            }
            else if (Split(line, WrittenPrefix) is (var ticks, var writtenPath) && Time(ticks) is { } time)
            {
                written[writtenPath] = time;
            }
        }

        // A first line saying neither is no record this build can read, and says nothing about the
        // build it describes: read as unordered, which costs one clean rebuild and nothing else.
        var unordered = lines[0] switch
        {
            OrderedMark => null,
            UnorderedMark => why ?? Unexplained,
            var mark => $"an unreadable record: its first line, '{mark}', says neither that the previous build "
                + "can be ordered nor that it cannot",
        };

        return new BuildRecord(unordered, variant, newest, contents, written);
    }

    /// <summary>
    /// The word after <paramref name="prefix"/> on <paramref name="line"/>, and the rest of the line after
    /// it; <see langword="null"/> where the line does not start with the prefix or holds no rest.
    /// </summary>
    private static (string Word, string Remainder)? Split(string line, string prefix)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var separator = line.IndexOf(' ', prefix.Length);

        return separator > prefix.Length && separator + 1 < line.Length
            ? (line[prefix.Length..separator], line[(separator + 1)..])
            : null;
    }

    private static string Ticks(DateTime time) => time.Ticks.ToString(CultureInfo.InvariantCulture);

    private static DateTime? Time(string ticks)
        => long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= DateTime.MinValue.Ticks
            && value <= DateTime.MaxValue.Ticks
            ? new DateTime(value, DateTimeKind.Utc)
            : null;
}
