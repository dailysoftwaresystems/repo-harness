namespace RepoHarness.Core.Projects;

/// <summary>
/// Recognises what kind of project a directory holds, so <c>init</c> can seed a
/// configuration that already matches the repository instead of an empty one.
/// </summary>
public interface IProjectDetector
{
    /// <summary>
    /// Returns the projects detected at <paramref name="treeRoot"/> in a fixed
    /// priority order (cmake, dart, then a solution or project file). The first entry seeds the
    /// default project. Empty when nothing recognisable is present.
    /// </summary>
    IReadOnlyList<DetectedProject> Detect(string treeRoot);
}

/// <summary>One recognised project.</summary>
/// <param name="Type">Adapter name: <c>cmake</c>, <c>dotnet</c> or <c>dart</c>.</param>
/// <param name="Path">
/// Path relative to the tree root: a directory for cmake and dart, the solution or project
/// file itself for dotnet.
/// </param>
/// <param name="Marker">The file that identified it, reported so the guess is auditable.</param>
public sealed record DetectedProject(string Type, string Path, string Marker);
