using System.Globalization;

namespace RepoHarness.Core.FileSystem;

/// <summary>The room on one filesystem.</summary>
/// <param name="FreeBytes">What the user asking can still write there.</param>
/// <param name="TotalBytes">How large the filesystem is.</param>
/// <param name="Filesystem">Where it is mounted - its drive on Windows - as a reader would look it up.</param>
public sealed record DiskSpace(long FreeBytes, long TotalBytes, string Filesystem)
{
    /// <summary>The room as it is said: <c>12.3 GiB free of 48 GiB on '/'</c>.</summary>
    public string Describe() => $"{Size(FreeBytes)} free of {Size(TotalBytes)} on '{Filesystem}'";

    /// <summary>
    /// <paramref name="bytes"/> in the largest binary unit it reaches, to one place: <c>12.3 GiB</c>,
    /// <c>512 MiB</c>, <c>3 bytes</c>.
    /// </summary>
    /// <param name="bytes">The size.</param>
    public static string Size(long bytes)
    {
        string[] units = ["KiB", "MiB", "GiB", "TiB"];

        if (bytes < 1024)
        {
            return bytes == 1 ? "1 byte" : string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
        }

        var value = bytes / 1024.0;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
