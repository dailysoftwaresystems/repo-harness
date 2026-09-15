using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Projects;

/// <inheritdoc cref="IProjectDetector"/>
public sealed class ProjectDetector(IFileSystem fileSystem) : IProjectDetector
{
    /// <summary>
    /// Marker files in priority order. Only the tree root is examined: guessing at
    /// depth produces confident nonsense on large repositories, and the point is to
    /// seed a starting configuration the user then edits, not to be authoritative.
    /// </summary>
    private static readonly (string Marker, string Type)[] Markers =
    [
        ("CMakeLists.txt", "cmake"),
        ("pubspec.yaml", "dart"),
    ];

    private readonly IFileSystem _fileSystem = fileSystem;

    public IReadOnlyList<DetectedProject> Detect(string treeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        var detected = new List<DetectedProject>();

        if (!_fileSystem.DirectoryExists(treeRoot))
        {
            return detected;
        }

        foreach (var (marker, type) in Markers)
        {
            if (_fileSystem.FileExists(Path.Combine(treeRoot, marker)))
            {
                detected.Add(new DetectedProject(type, ".", marker));
            }
        }

        var names = _fileSystem
            .EnumerateFiles(treeRoot, recursive: false)
            .Select(file => Path.GetFileName(file))
            .OfType<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A solution names the whole .NET build. A repository with project files but no
        // solution is still recognised, through its first project file.
        var dotnet = names.FirstOrDefault(IsSolution) ?? names.FirstOrDefault(IsProject);

        if (dotnet is not null)
        {
            detected.Add(new DetectedProject("dotnet", dotnet, dotnet));
        }

        return detected;
    }

    private static bool IsSolution(string name)
        => name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static bool IsProject(string name)
        => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
}
