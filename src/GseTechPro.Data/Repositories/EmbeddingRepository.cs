using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

/// <summary>Small vec0 persistence/KNN helper keyed by the relational manual_chunks.id.</summary>
public sealed class EmbeddingRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public EmbeddingRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Checks both the vec0 module and the embeddings table.</summary>
    public async Task<bool> IsVec0AvailableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await IsReadyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces one chunk's 768-dimensional vector. Returns false without writing when vec0 is
    /// unavailable or the chunk/manual is missing or soft-deleted.
    /// </summary>
    public async Task<bool> StoreAsync(
        long chunkId,
        ReadOnlyMemory<float> embedding,
        CancellationToken cancellationToken = default)
    {
        ValidateVector(embedding, nameof(embedding));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsReadyAsync(connection, cancellationToken).ConfigureAwait(false))
            return false;

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using (var live = connection.CreateCommand())
            {
                live.Transaction = transaction;
                live.CommandText = """
                    SELECT 1
                    FROM manual_chunks AS c
                    INNER JOIN manuals AS m ON m.id = c.manual_id AND m.deleted_at IS NULL
                    WHERE c.id = $chunkId AND c.deleted_at IS NULL;
                    """;
                live.Parameters.AddWithValue("$chunkId", chunkId);
                if (await live.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM embeddings WHERE chunk_id = $chunkId;";
                delete.Parameters.AddWithValue("$chunkId", chunkId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO embeddings(chunk_id, embedding)
                    VALUES ($chunkId, $embedding);
                    """;
                insert.Parameters.AddWithValue("$chunkId", chunkId);
                insert.Parameters.Add("$embedding", SqliteType.Blob).Value = SerializeVector(embedding);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns at most <paramref name="limit"/> live chunk ids in ascending vec0 L2 distance.
    /// Returns an empty list when vector storage is unavailable.
    /// </summary>
    public async Task<IReadOnlyList<long>> FindNearestChunkIdsAsync(
        ReadOnlyMemory<float> query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        ValidateVector(query, nameof(query));
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The KNN limit must be positive.");

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsReadyAsync(connection, cancellationToken).ConfigureAwait(false))
            return Array.Empty<long>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.chunk_id
            FROM embeddings AS e
            INNER JOIN manual_chunks AS c
                ON c.id = e.chunk_id AND c.deleted_at IS NULL
            INNER JOIN manuals AS m
                ON m.id = c.manual_id AND m.deleted_at IS NULL
            WHERE e.embedding MATCH $query
              AND k = $limit
            ORDER BY e.distance;
            """;
        command.Parameters.Add("$query", SqliteType.Blob).Value = SerializeVector(query);
        command.Parameters.AddWithValue("$limit", limit);

        var ids = new List<long>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    internal static async Task<bool> IsReadyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await SqliteVecExtension.IsVec0AvailableAsync(connection, cancellationToken).ConfigureAwait(false))
            return false;

        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'embeddings';";
        return await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Deletes one vector within the caller's soft-delete transaction.</summary>
    internal static async Task DeleteForChunkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long chunkId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM embeddings WHERE chunk_id = $chunkId;";
        command.Parameters.AddWithValue("$chunkId", chunkId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes every manual vector within the caller's soft-delete transaction.</summary>
    internal static async Task DeleteForManualAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string manualId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM embeddings
            WHERE chunk_id IN (
                SELECT id FROM manual_chunks WHERE manual_id = $manualId
            );
            """;
        command.Parameters.AddWithValue("$manualId", manualId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateVector(ReadOnlyMemory<float> vector, string parameterName)
    {
        if (vector.Length != SqliteVecExtension.EmbeddingDimensions)
        {
            throw new ArgumentException(
                $"Embeddings must contain exactly {SqliteVecExtension.EmbeddingDimensions} floats.",
                parameterName);
        }

        for (var i = 0; i < vector.Length; i++)
        {
            if (!float.IsFinite(vector.Span[i]))
                throw new ArgumentException("Embeddings cannot contain NaN or infinity.", parameterName);
        }
    }

    private static byte[] SerializeVector(ReadOnlyMemory<float> vector)
    {
        // vec0's binary vector representation is contiguous little-endian float32 values.
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector.Span[i]);
        return bytes;
    }
}
