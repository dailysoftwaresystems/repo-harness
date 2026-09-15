namespace RepoHarness.Core.Repository;

/// <summary>
/// Finds the repository a command should act on, the way git does: by looking at
/// the current directory and walking upwards.
/// </summary>
public interface IRepositoryLocator
{
    /// <summary>
    /// Resolves the layout for <paramref name="startDirectory"/>, or
    /// <see langword="null"/> when it is not inside a git repository.
    /// </summary>
    /// <exception cref="RepoHarness.Core.Results.HarnessException">
    /// git could not inspect the directory, or could not locate the main checkout.
    /// </exception>
    Task<HarnessLayout?> LocateAsync(string startDirectory, CancellationToken cancellationToken = default);
}
