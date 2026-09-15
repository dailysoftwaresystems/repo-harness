namespace RepoHarness.Core.Configuration;

/// <summary>
/// Raised when configuration is missing, unparseable, or internally inconsistent.
/// Callers translate this into the "invalid configuration" exit code, which is
/// deliberately distinct from "the command you asked for failed".
/// </summary>
public sealed class ConfigException(string message, Exception? innerException = null)
    : Exception(message, innerException);
