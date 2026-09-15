namespace RepoHarness.Core.Configuration;

/// <summary>Reads and writes <c>config.json</c>.</summary>
public interface IConfigStore
{
    /// <summary>Loads configuration from <paramref name="path"/>.</summary>
    /// <exception cref="ConfigException">The file is missing or cannot be parsed.</exception>
    HarnessConfig Load(string path);

    /// <summary>Writes configuration to <paramref name="path"/>, replacing it atomically.</summary>
    void Save(string path, HarnessConfig config);

    /// <summary>Renders configuration as the JSON that would be written.</summary>
    string Serialize(HarnessConfig config);
}
