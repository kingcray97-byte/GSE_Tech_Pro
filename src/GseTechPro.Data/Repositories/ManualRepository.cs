using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

public sealed record ManualUpsertResult(Manual Manual, bool Inserted);

/// <summary>Manual catalog persistence with content-hash dedupe and graph soft-delete.</summary>
public sealed class ManualRepository : SqliteRepository<Manual>
{
    private const string SelectColumns =
        "id, title, manufacturer, model_family, file_path, file_hash, page_count, format, " +
        "status, imported_at, created_at, updated_at, deleted_at";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public ManualRepository(ISqliteConnectionFactory connectionFactory)
        : base(connectionFactory, "manuals")
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>Finds one live manual by its content hash.</summary>
    public async Task<Manual?> GetByFileHashAsync(
        string fileHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetByHashAsync(connection, null, fileHash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts a manual, or returns the existing live row with the same file hash. The lookup and
    /// insert share a serializable transaction so a repeated/import-retried file is not duplicated.
    /// </summary>
    public async Task<ManualUpsertResult> UpsertAsync(
        Manual manual,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manual);
        Validate(manual);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var existing = await GetByHashAsync(
                connection, transaction, manual.FileHash, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ManualUpsertResult(existing, false);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"""
                    INSERT INTO manuals ({SelectColumns})
                    VALUES (
                      $id, $title, $manufacturer, $modelFamily, $filePath, $fileHash, $pageCount,
                      $format, $status, $importedAt, $createdAt, $updatedAt, $deletedAt);
                    """;
                insert.Parameters.AddWithValue("$id", manual.Id);
                insert.Parameters.AddWithValue("$title", manual.Title);
                insert.Parameters.AddWithNullableValue("$manufacturer", manual.Manufacturer);
                insert.Parameters.AddWithNullableValue("$modelFamily", manual.ModelFamily);
                insert.Parameters.AddWithValue("$filePath", manual.FilePath);
                insert.Parameters.AddWithValue("$fileHash", manual.FileHash);
                insert.Parameters.AddWithNullableValue("$pageCount", manual.PageCount);
                insert.Parameters.AddWithValue("$format", DbEnum.ToCode(manual.Format));
                insert.Parameters.AddWithValue("$status", DbEnum.ToCode(manual.Status));
                insert.Parameters.AddWithValue("$importedAt", manual.ImportedAt);
                insert.Parameters.AddWithValue("$createdAt", manual.CreatedAt);
                insert.Parameters.AddWithValue("$updatedAt", manual.UpdatedAt);
                insert.Parameters.AddWithNullableValue("$deletedAt", manual.DeletedAt);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ManualUpsertResult(manual, true);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Soft-deletes a live manual and all its live chunks atomically. manual_pages has no
    /// deleted_at column in the approved schema, so its physical rows are retained and inherit
    /// the parent's tombstone: ManualPageRepository hides them while the manual is deleted.
    /// </summary>
    public async Task<bool> SoftDeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Probe before opening the transaction so every command participates correctly in it.
        var embeddingsReady = await EmbeddingRepository.IsReadyAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var deletedAt = Clock.UtcNow;
            int affected;
            await using (var manual = connection.CreateCommand())
            {
                manual.Transaction = transaction;
                manual.CommandText = """
                    UPDATE manuals
                    SET deleted_at = $deletedAt, updated_at = $deletedAt
                    WHERE id = $id AND deleted_at IS NULL;
                    """;
                manual.Parameters.AddWithValue("$deletedAt", deletedAt);
                manual.Parameters.AddWithValue("$id", id);
                affected = await manual.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (affected != 0)
            {
                await using var chunks = connection.CreateCommand();
                chunks.Transaction = transaction;
                chunks.CommandText = """
                    UPDATE manual_chunks
                    SET deleted_at = $deletedAt, updated_at = $deletedAt
                    WHERE manual_id = $id AND deleted_at IS NULL;
                    """;
                chunks.Parameters.AddWithValue("$deletedAt", deletedAt);
                chunks.Parameters.AddWithValue("$id", id);
                await chunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (embeddingsReady)
                {
                    await EmbeddingRepository.DeleteForManualAsync(
                        connection, transaction, id, cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected == 1;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Uses the cascading soft-delete for repository callers as well.</summary>
    public new Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        SoftDeleteAsync(id, cancellationToken);

    private static async Task<Manual?> GetByHashAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string fileHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM manuals
            WHERE file_hash = $fileHash AND deleted_at IS NULL
            ORDER BY created_at, id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$fileHash", fileHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadManual(reader) : null;
    }

    private static Manual ReadManual(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        Manufacturer = reader.IsDBNull(2) ? null : reader.GetString(2),
        ModelFamily = reader.IsDBNull(3) ? null : reader.GetString(3),
        FilePath = reader.GetString(4),
        FileHash = reader.GetString(5),
        PageCount = reader.IsDBNull(6) ? null : reader.GetInt32(6),
        Format = (ManualFormat)DbValueConverter.FromDatabase(reader.GetString(7), typeof(ManualFormat))!,
        Status = (ManualStatus)DbValueConverter.FromDatabase(reader.GetString(8), typeof(ManualStatus))!,
        ImportedAt = reader.GetString(9),
        CreatedAt = reader.GetString(10),
        UpdatedAt = reader.GetString(11),
        DeletedAt = reader.IsDBNull(12) ? null : reader.GetString(12),
    };

    private static void Validate(Manual manual)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manual.Id, nameof(manual));
        ArgumentException.ThrowIfNullOrWhiteSpace(manual.Title, nameof(manual));
        ArgumentException.ThrowIfNullOrWhiteSpace(manual.FilePath, nameof(manual));
        ArgumentException.ThrowIfNullOrWhiteSpace(manual.FileHash, nameof(manual));
        if (manual.PageCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(manual), "Page count cannot be negative.");
    }
}

internal static class SqliteParameterCollectionExtensions
{
    public static void AddWithNullableValue(
        this SqliteParameterCollection parameters,
        string name,
        object? value) => parameters.AddWithValue(name, value ?? DBNull.Value);
}
