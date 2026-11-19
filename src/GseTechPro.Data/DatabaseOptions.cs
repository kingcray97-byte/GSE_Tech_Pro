namespace GseTechPro.Data;

/// <summary>Configuration for the per-tablet SQLite database.</summary>
public sealed record DatabaseOptions
{
    /// <summary>The default per-user, per-tablet database location.</summary>
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GseTechPro",
        "gse-tech-pro.db");

    /// <summary>Database file path. Tests can override this with a temporary file path.</summary>
    public string DatabasePath { get; init; } = DefaultDatabasePath;

    /// <summary>
    /// Optional path to the trusted sqlite-vec extension (normally vec0.dll). When omitted,
    /// only application-local vec0.dll and runtimes/win-x64/native/vec0.dll are probed.
    /// </summary>
    public string? SqliteVecExtensionPath { get; init; }

    /// <summary>Whether to probe for sqlite-vec after relational migrations. Failure is non-fatal.</summary>
    public bool EnableVectorSearch { get; init; } = true;

    /// <summary>Optional local diagnostic sink for migration notices (for example, the app log).</summary>
    public Action<string>? LogNotice { get; init; }
}
