namespace Collector.Extensions;

/// <summary>
/// Resolves the data directory (tracker.db, api.db, logs, audio) shared by the
/// collector and the API.
/// </summary>
public static class DataDirectory
{
    /// <summary>Environment variable that overrides the data directory.</summary>
    public const string EnvironmentVariable = "COLLECTOR_DATA_DIR";

    /// <summary>
    /// Returns <paramref name="configured"/> as an absolute path (relative values are
    /// resolved against <paramref name="baseDirectory"/>), or the historical default:
    /// the <c>data</c> folder two levels above the binaries (<c>bin/&lt;app&gt;/net10.0</c>).
    /// </summary>
    public static string Resolve(string? configured, string baseDirectory)
    {
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(baseDirectory, "..", "..", "data")
            : Path.Combine(baseDirectory, configured.Trim());
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// In Production the data directory must already hold tracker.db: a missing or
    /// mistyped <see cref="EnvironmentVariable"/> would otherwise start a new, empty
    /// database and every page would look empty. To create one on purpose, create an
    /// empty tracker.db file first. Development and tests create it freely.
    /// </summary>
    public static void EnsureExistingDatabase(string dataDir, bool production)
    {
        if (production && !File.Exists(Path.Combine(dataDir, "tracker.db")))
        {
            throw new InvalidOperationException(
                $"No tracker.db in {dataDir}. Set {EnvironmentVariable} to the data directory; " +
                "Production never creates a new database (create an empty tracker.db file to start one).");
        }
    }

    /// <summary>
    /// Resolves the data directory for the current process: <see cref="EnvironmentVariable"/>,
    /// then <paramref name="configured"/>, then the default.
    /// </summary>
    public static string ResolveForCurrentProcess(string? configured = null) =>
        Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable) ?? configured, AppContext.BaseDirectory);
}
