using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Execution;

/// <summary>What a leg's build directory held, whether it was removed, and the room on its filesystem.</summary>
/// <param name="Directory">The build directory, on the machine that holds it.</param>
/// <param name="BuildBytes">What it held: what was removed, or, where nothing was, what is there.</param>
/// <param name="Removed">Whether it was removed.</param>
/// <param name="Disk">
/// The room on its filesystem - after the removal, where there was one - or <see langword="null"/> where it
/// could not be measured.
/// </param>
public sealed record BuildSpace(string Directory, long BuildBytes, bool Removed, DiskSpace? Disk);
