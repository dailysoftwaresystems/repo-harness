namespace RepoHarness.Core.Execution;

/// <summary>The environment a process a leg starts is given, built from the layers that declare it.</summary>
public static class PhaseEnvironment
{
    /// <summary>
    /// <paramref name="layers"/> merged in order, each over the ones before it: a name a later layer
    /// sets is the value the process sees.
    /// </summary>
    /// <param name="layers">From the least specific - the machine's own - to the most.</param>
    /// <remarks>
    /// Names compare ignoring case on every platform, as they do on Windows and as every environment
    /// the configuration declares compares them, so a layer that sets PATH replaces a lower layer's
    /// Path rather than setting a second variable beside it - its spelling along with its value, as
    /// the more specific layer is the one that says what the program reads.
    /// </remarks>
    public static Dictionary<string, string?> Layered(params IEnumerable<KeyValuePair<string, string>>[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in layers)
        {
            foreach (var (name, value) in layer)
            {
                environment.Remove(name);
                environment[name] = value;
            }
        }

        return environment;
    }
}
