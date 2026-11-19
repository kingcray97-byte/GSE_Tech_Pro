using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

/// <summary>A BM25 search hit. FTS5 bm25() is lower-is-better; results are ordered accordingly.</summary>
public sealed record ManualChunkSearchResult(ManualChunk Chunk, double Score);

/// <summary>
/// Manual-chunk storage, rowid/GUID lookup, page-window retrieval, and BM25 search. The integer id
/// is assigned by SQLite and is the shared key used by manual_chunks_fts and embeddings.
/// </summary>
public sealed class ManualChunkRepository
{
    private const string SelectColumns =
        "c.id, c.guid, c.manual_id, c.page_start, c.page_end, c.section, c.chunk_kind, " +
        "c.heading, c.chunk_text, c.created_at, c.updated_at, c.deleted_at";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public ManualChunkRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Inserts one chunk and returns it with its database-assigned rowid.</summary>
    public async Task<ManualChunk> InsertAsync(
        ManualChunk chunk,
        CancellationToken cancellationToken = default)
    {
        var rows = await InsertAsync([chunk], cancellationToken).ConfigureAwait(false);
        return rows[0];
    }

    /// <summary>Inserts a batch atomically and returns every chunk with its assigned rowid/GUID.</summary>
    public async Task<IReadOnlyList<ManualChunk>> InsertAsync(
        IEnumerable<ManualChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var inserted = new List<ManualChunk>();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO manual_chunks
                    (guid, manual_id, page_start, page_end, section, chunk_kind, heading,
                     chunk_text, created_at, updated_at, deleted_at)
                VALUES
                    ($guid, $manualId, $pageStart, $pageEnd, $section, $chunkKind, $heading,
                     $chunkText, $createdAt, $updatedAt, $deletedAt);
                SELECT last_insert_rowid();
                """;

            var guid = command.Parameters.Add("$guid", SqliteType.Text);
            var manualId = command.Parameters.Add("$manualId", SqliteType.Text);
            var pageStart = command.Parameters.Add("$pageStart", SqliteType.Integer);
            var pageEnd = command.Parameters.Add("$pageEnd", SqliteType.Integer);
            var section = command.Parameters.Add("$section", SqliteType.Text);
            var chunkKind = command.Parameters.Add("$chunkKind", SqliteType.Text);
            var heading = command.Parameters.Add("$heading", SqliteType.Text);
            var chunkText = command.Parameters.Add("$chunkText", SqliteType.Text);
            var createdAt = command.Parameters.Add("$createdAt", SqliteType.Text);
            var updatedAt = command.Parameters.Add("$updatedAt", SqliteType.Text);
            var deletedAt = command.Parameters.Add("$deletedAt", SqliteType.Text);

            foreach (var chunk in chunks)
            {
                Validate(chunk);
                guid.Value = chunk.Guid;
                manualId.Value = chunk.ManualId;
                pageStart.Value = chunk.PageStart;
                pageEnd.Value = chunk.PageEnd;
                section.Value = DbValueConverter.ToDatabase(chunk.Section);
                chunkKind.Value = DbEnum.ToCode(chunk.ChunkKind);
                heading.Value = DbValueConverter.ToDatabase(chunk.Heading);
                chunkText.Value = chunk.ChunkText;
                createdAt.Value = chunk.CreatedAt;
                updatedAt.Value = chunk.UpdatedAt;
                deletedAt.Value = DbValueConverter.ToDatabase(chunk.DeletedAt);

                var id = Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                inserted.Add(chunk with { Id = id });
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return inserted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<ManualChunk?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        GetOneAsync("c.id", id, cancellationToken);

    public Task<ManualChunk?> GetByGuidAsync(string guid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guid);
        return GetOneAsync("c.guid", guid, cancellationToken);
    }

    /// <summary>
    /// Lists live chunks for a manual, optionally restricted to chunks overlapping an inclusive
    /// page range. A soft-deleted parent manual hides all of its chunks.
    /// </summary>
    public async Task<IReadOnlyList<ManualChunk>> ListByManualAsync(
        string manualId,
        int? pageStart = null,
        int? pageEnd = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manualId);
        if (pageStart.HasValue != pageEnd.HasValue)
            throw new ArgumentException("Both pageStart and pageEnd must be supplied together.");
        if (pageStart <= 0 || pageEnd <= 0 || pageEnd < pageStart)
            throw new ArgumentOutOfRangeException(nameof(pageStart), "The page range must be positive and ordered.");

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateLiveSelect(connection);
        command.CommandText += """

              AND c.manual_id = $manualId
            """ + (pageStart.HasValue
            ? """

              AND c.page_start <= $pageEnd
              AND c.page_end >= $pageStart
              """
            : string.Empty) + """

            ORDER BY c.page_start, c.id;
            """;
        command.Parameters.AddWithValue("$manualId", manualId);
        if (pageStart.HasValue)
        {
            command.Parameters.AddWithValue("$pageStart", pageStart.Value);
            command.Parameters.AddWithValue("$pageEnd", pageEnd!.Value);
        }

        return await ReadChunksAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// BM25 search over chunk_text/heading/section. It explicitly excludes soft-deleted chunks
    /// (FTS5 still indexes them) and chunks hidden by a soft-deleted manual.
    /// </summary>
    public async Task<IReadOnlyList<ManualChunkSearchResult>> SearchFtsAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The FTS result limit must be positive.");

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}, bm25(manual_chunks_fts) AS score
            FROM manual_chunks_fts
            INNER JOIN manual_chunks AS c ON c.id = manual_chunks_fts.rowid
            INNER JOIN manuals AS m ON m.id = c.manual_id AND m.deleted_at IS NULL
            WHERE manual_chunks_fts MATCH $query
              AND c.deleted_at IS NULL
            ORDER BY score, c.id
            LIMIT $limit;
            """;
        // Treat input as user search text, not a raw MATCH expression. Phrase-quoting each
        // whitespace-delimited token keeps hyphens, question marks, and boolean words literal.
        command.Parameters.AddWithValue("$query", CreateSafeMatchQuery(query));
        command.Parameters.AddWithValue("$limit", limit);

        try
        {
            var results = new List<ManualChunkSearchResult>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(new ManualChunkSearchResult(ReadChunk(reader), reader.GetDouble(12)));
            return results;
        }
        catch (SqliteException ex) when (IsFtsSyntaxError(ex))
        {
            // Some tokenizers reject a phrase containing only punctuation. User text should
            // produce no hits rather than break the RAG/search workflow.
            return Array.Empty<ManualChunkSearchResult>();
        }
    }

    /// <summary>
    /// Soft-deletes one chunk and removes its vec0 row in the same transaction. FTS history is
    /// retained externally, but BM25 queries exclude the relational tombstone.
    /// </summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var embeddingsReady = await EmbeddingRepository.IsReadyAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            int affected;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE manual_chunks
                    SET deleted_at = $deletedAt, updated_at = $deletedAt
                    WHERE id = $id
                      AND deleted_at IS NULL
                      AND EXISTS (
                          SELECT 1 FROM manuals
                          WHERE manuals.id = manual_chunks.manual_id AND manuals.deleted_at IS NULL
                      );
                    """;
                command.Parameters.AddWithValue("$deletedAt", Clock.UtcNow);
                command.Parameters.AddWithValue("$id", id);
                affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (affected == 1 && embeddingsReady)
            {
                await EmbeddingRepository.DeleteForChunkAsync(
                    connection, transaction, id, cancellationToken).ConfigureAwait(false);
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

    private async Task<ManualChunk?> GetOneAsync(
        string keyColumn,
        object key,
        CancellationToken cancellationToken)
    {
        // keyColumn is selected only by GetByIdAsync/GetByGuidAsync, never by external input.
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateLiveSelect(connection);
        command.CommandText += $"""

              AND {keyColumn} = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);
        var rows = await ReadChunksAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static SqliteCommand CreateLiveSelect(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM manual_chunks AS c
            INNER JOIN manuals AS m ON m.id = c.manual_id AND m.deleted_at IS NULL
            WHERE c.deleted_at IS NULL
            """;
        return command;
    }

    private static async Task<IReadOnlyList<ManualChunk>> ReadChunksAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<ManualChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadChunk(reader));
        return rows;
    }

    private static ManualChunk ReadChunk(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Guid = reader.GetString(1),
        ManualId = reader.GetString(2),
        PageStart = reader.GetInt32(3),
        PageEnd = reader.GetInt32(4),
        Section = reader.IsDBNull(5) ? null : reader.GetString(5),
        ChunkKind = (ChunkKind)DbValueConverter.FromDatabase(reader.GetString(6), typeof(ChunkKind))!,
        Heading = reader.IsDBNull(7) ? null : reader.GetString(7),
        ChunkText = reader.GetString(8),
        CreatedAt = reader.GetString(9),
        UpdatedAt = reader.GetString(10),
        DeletedAt = reader.IsDBNull(11) ? null : reader.GetString(11),
    };

    private static string CreateSafeMatchQuery(string query)
    {
        var tokens = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", tokens.Select(token => $"\"{token.Replace("\"", "\"\"")}\""));
    }

    private static bool IsFtsSyntaxError(SqliteException exception) =>
        exception.SqliteErrorCode == 1 &&
        (exception.Message.Contains("fts5: syntax error", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("malformed MATCH expression", StringComparison.OrdinalIgnoreCase));

    private static void Validate(ManualChunk? chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.Guid, nameof(chunk));
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.ManualId, nameof(chunk));
        ArgumentException.ThrowIfNullOrWhiteSpace(chunk.ChunkText, nameof(chunk));
        if (chunk.PageStart <= 0 || chunk.PageEnd < chunk.PageStart)
            throw new ArgumentOutOfRangeException(nameof(chunk), "Chunk page numbers must be positive and ordered.");
    }
}
