using System.Data;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data;

/// <summary>
/// Loads the trusted, bundled sqlite-vec extension and probes the vec0 module. sqlite-vec is
/// optional at runtime: load/probe failures leave FTS5 and all relational storage usable.
/// The bundled upstream binary is dual-licensed MIT / Apache-2.0; see Native/README.md.
/// </summary>
public static class SqliteVecExtension
{
    public const int EmbeddingDimensions = 768;

    /// <summary>
    /// Reports whether vec0 is usable on an open connection. This checks the module, not whether
    /// the embeddings table has been created; use <c>DataServices.IsVec0AvailableAsync</c> for the
    /// complete vector-storage readiness probe.
    /// </summary>
    public static async Task<bool> IsVec0AvailableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The SQLite connection must be open for the vec0 probe.");

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT vec_version();";
            var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return version is not null && version is not DBNull;
        }
        catch (SqliteException)
        {
            // A missing extension normally surfaces as "no such function: vec_version".
            return false;
        }
    }

    internal static bool IsMissingVec0(SqliteException exception) =>
        exception.SqliteErrorCode == 1 &&
        exception.Message.Contains("vec0", StringComparison.OrdinalIgnoreCase) &&
        exception.Message.Contains("module", StringComparison.OrdinalIgnoreCase);

    internal static async Task<bool> TryLoadAsync(
        SqliteConnection connection,
        string extensionPath,
        Action<string> unavailable,
        CancellationToken cancellationToken)
    {
        if (await IsVec0AvailableAsync(connection, cancellationToken).ConfigureAwait(false))
            return true;

        var extensionsEnabled = false;
        try
        {
            connection.EnableExtensions(true);
            extensionsEnabled = true;

            await using var load = connection.CreateCommand();
            load.CommandText = "SELECT load_extension($extensionPath, $entryPoint);";
            load.Parameters.AddWithValue("$extensionPath", extensionPath);
            load.Parameters.AddWithValue("$entryPoint", "sqlite3_vec_init");
            await load.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException
            or InvalidOperationException
            or DllNotFoundException
            or EntryPointNotFoundException
            or NotSupportedException
            or BadImageFormatException
            or IOException
            or UnauthorizedAccessException)
        {
            unavailable($"sqlite-vec could not be loaded; vector search is disabled. {ex.Message}");
            return false;
        }
        finally
        {
            if (extensionsEnabled)
                connection.EnableExtensions(false);
        }

        if (await IsVec0AvailableAsync(connection, cancellationToken).ConfigureAwait(false))
            return true;

        unavailable("sqlite-vec loaded but the vec0 probe failed; vector search is disabled.");
        return false;
    }

    internal static string? ResolveExtensionPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;

        // Only explicit, trusted application-local paths are probed. Do not search PATH or load an
        // arbitrary native library from the working directory.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vec0.dll"),
            Path.Combine(AppContext.BaseDirectory, "vec0.dll"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
