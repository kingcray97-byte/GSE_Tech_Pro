using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

/// <summary>Idempotent manufacturer/model/system tags for manual chunks.</summary>
public sealed class ChunkTagRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public ChunkTagRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Adds a tag if absent; returns false when the (chunk_id, tag) pair already exists.</summary>
    public async Task<bool> AddAsync(ChunkTag tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var added = await AddRangeAsync(tag.ChunkId, [tag.Tag], cancellationToken).ConfigureAwait(false);
        return added == 1;
    }

    /// <summary>Adds distinct tags atomically and returns the number newly inserted.</summary>
    public async Task<int> AddRangeAsync(
        long chunkId,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var inserted = 0;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO chunk_tags(chunk_id, tag) VALUES ($chunkId, $tag);";
            command.Parameters.AddWithValue("$chunkId", chunkId);
            var tag = command.Parameters.Add("$tag", SqliteType.Text);

            foreach (var value in tags)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(tags));
                tag.Value = value.Trim();
                inserted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<ChunkTag>> ListByChunkAsync(
        long chunkId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.chunk_id, t.tag
            FROM chunk_tags AS t
            INNER JOIN manual_chunks AS c ON c.id = t.chunk_id AND c.deleted_at IS NULL
            INNER JOIN manuals AS m ON m.id = c.manual_id AND m.deleted_at IS NULL
            WHERE t.chunk_id = $chunkId
            ORDER BY t.tag;
            """;
        command.Parameters.AddWithValue("$chunkId", chunkId);

        var rows = new List<ChunkTag>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new ChunkTag { ChunkId = reader.GetInt64(0), Tag = reader.GetString(1) });
        return rows;
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(
        long chunkId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ListByChunkAsync(chunkId, cancellationToken).ConfigureAwait(false);
        return rows.Select(row => row.Tag).ToArray();
    }
}
