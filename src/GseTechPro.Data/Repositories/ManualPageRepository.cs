using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

/// <summary>
/// Page metadata storage. Pages inherit manual deletion through the parent join because the
/// approved manual_pages schema intentionally has no deleted_at column.
/// </summary>
public sealed class ManualPageRepository
{
    private const string SelectColumns =
        "p.id, p.guid, p.manual_id, p.page_number, p.section, p.has_image, p.thumbnail_path, p.created_at";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public ManualPageRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<ManualPage> InsertAsync(
        ManualPage page,
        CancellationToken cancellationToken = default)
    {
        var rows = await InsertAsync([page], cancellationToken).ConfigureAwait(false);
        return rows[0];
    }

    public async Task<IReadOnlyList<ManualPage>> InsertAsync(
        IEnumerable<ManualPage> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var inserted = new List<ManualPage>();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO manual_pages
                    (guid, manual_id, page_number, section, has_image, thumbnail_path, created_at)
                VALUES
                    ($guid, $manualId, $pageNumber, $section, $hasImage, $thumbnailPath, $createdAt);
                SELECT last_insert_rowid();
                """;

            var guid = command.Parameters.Add("$guid", SqliteType.Text);
            var manualId = command.Parameters.Add("$manualId", SqliteType.Text);
            var pageNumber = command.Parameters.Add("$pageNumber", SqliteType.Integer);
            var section = command.Parameters.Add("$section", SqliteType.Text);
            var hasImage = command.Parameters.Add("$hasImage", SqliteType.Integer);
            var thumbnailPath = command.Parameters.Add("$thumbnailPath", SqliteType.Text);
            var createdAt = command.Parameters.Add("$createdAt", SqliteType.Text);

            foreach (var page in pages)
            {
                Validate(page);
                guid.Value = page.Guid;
                manualId.Value = page.ManualId;
                pageNumber.Value = page.PageNumber;
                section.Value = DbValueConverter.ToDatabase(page.Section);
                hasImage.Value = page.HasImage ? 1L : 0L;
                thumbnailPath.Value = DbValueConverter.ToDatabase(page.ThumbnailPath);
                createdAt.Value = page.CreatedAt;

                var id = Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                inserted.Add(page with { Id = id });
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

    public Task<ManualPage?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        GetOneAsync("p.id", id, cancellationToken);

    public Task<ManualPage?> GetByGuidAsync(string guid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guid);
        return GetOneAsync("p.guid", guid, cancellationToken);
    }

    public async Task<IReadOnlyList<ManualPage>> ListByManualAsync(
        string manualId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manualId);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateLiveSelect(connection);
        command.CommandText += """

              AND p.manual_id = $manualId
            ORDER BY p.page_number, p.id;
            """;
        command.Parameters.AddWithValue("$manualId", manualId);
        return await ReadPagesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ManualPage?> GetOneAsync(
        string keyColumn,
        object key,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateLiveSelect(connection);
        command.CommandText += $"""

              AND {keyColumn} = $key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);
        var rows = await ReadPagesAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static SqliteCommand CreateLiveSelect(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM manual_pages AS p
            INNER JOIN manuals AS m ON m.id = p.manual_id AND m.deleted_at IS NULL
            WHERE 1 = 1
            """;
        return command;
    }

    private static async Task<IReadOnlyList<ManualPage>> ReadPagesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<ManualPage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ManualPage
            {
                Id = reader.GetInt64(0),
                Guid = reader.GetString(1),
                ManualId = reader.GetString(2),
                PageNumber = reader.GetInt32(3),
                Section = reader.IsDBNull(4) ? null : reader.GetString(4),
                HasImage = reader.GetInt64(5) != 0,
                ThumbnailPath = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = reader.GetString(7),
            });
        }
        return rows;
    }

    private static void Validate(ManualPage? page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(page.Guid, nameof(page));
        ArgumentException.ThrowIfNullOrWhiteSpace(page.ManualId, nameof(page));
        if (page.PageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(page), "Manual page numbers are one-based.");
    }
}
