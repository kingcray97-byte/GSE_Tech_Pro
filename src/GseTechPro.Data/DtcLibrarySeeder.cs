using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data;

/// <summary>Counts for the embedded source rows handled by one DTC library import.</summary>
public sealed record DtcLibrarySeedResult(int InsertedCount, int AlreadyPresentCount)
{
    public int SourceRowCount => InsertedCount + AlreadyPresentCount;
}

/// <summary>Imports the offline, authored J1939/J1587/OBD II reference library.</summary>
public interface IDtcLibrarySeeder
{
    Task<DtcLibrarySeedResult> SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Inserts missing DTC reference rows by their exact (protocol, code) key. Existing rows are never
/// updated, so technician/OEM edits survive every application startup and repeated explicit import.
/// </summary>
public sealed class DtcLibrarySeeder : IDtcLibrarySeeder
{
    private static readonly string[] ResourceNames =
    [
        "GseTechPro.Data.Content.Dtc.j1939.json",
        "GseTechPro.Data.Content.Dtc.j1587.json",
        "GseTechPro.Data.Content.Dtc.obd2.json",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ISqliteConnectionFactory _connectionFactory;

    public DtcLibrarySeeder(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Loads and validates all three embedded resources, then transactionally inserts only missing
    /// keys. A clean 540-row import reports 540 inserted; an immediate repeat reports 540 present.
    /// </summary>
    public async Task<DtcLibrarySeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var sourceRows = await LoadSourceRowsAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var existingKeys = await ReadExistingKeysAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var inserted = 0;
            var alreadyPresent = 0;

            await using var insert = CreateInsertCommand(connection, transaction);
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var protocol = insert.Parameters.Add("$protocol", SqliteType.Text);
            var code = insert.Parameters.Add("$code", SqliteType.Text);
            var spn = insert.Parameters.Add("$spn", SqliteType.Integer);
            var fmi = insert.Parameters.Add("$fmi", SqliteType.Integer);
            var description = insert.Parameters.Add("$description", SqliteType.Text);
            var system = insert.Parameters.Add("$system", SqliteType.Text);
            var severity = insert.Parameters.Add("$severity", SqliteType.Text);
            var likelyCauses = insert.Parameters.Add("$likelyCauses", SqliteType.Text);
            var sourceCitation = insert.Parameters.Add("$sourceCitation", SqliteType.Text);
            var createdAt = insert.Parameters.Add("$createdAt", SqliteType.Text);
            var updatedAt = insert.Parameters.Add("$updatedAt", SqliteType.Text);

            foreach (var sourceRow in sourceRows)
            {
                var key = new DtcKey(DbEnum.ToCode(sourceRow.Protocol), sourceRow.Code);
                if (!existingKeys.Add(key))
                {
                    alreadyPresent++;
                    continue;
                }

                // Constructing the Core row here ensures IDs and timestamps are generated only when
                // an insert is needed, never for rows that are already present.
                var entry = ToLibraryEntry(sourceRow);
                id.Value = entry.Id;
                protocol.Value = DbEnum.ToCode(entry.Protocol);
                code.Value = entry.Code;
                spn.Value = (object?)entry.Spn ?? DBNull.Value;
                fmi.Value = (object?)entry.Fmi ?? DBNull.Value;
                description.Value = (object?)entry.Description ?? DBNull.Value;
                system.Value = (object?)entry.System ?? DBNull.Value;
                severity.Value = (object?)entry.Severity ?? DBNull.Value;
                likelyCauses.Value = (object?)entry.LikelyCauses ?? DBNull.Value;
                sourceCitation.Value = (object?)entry.SourceCitation ?? DBNull.Value;
                createdAt.Value = entry.CreatedAt;
                updatedAt.Value = entry.UpdatedAt;

                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                    inserted++;
                else
                    alreadyPresent++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DtcLibrarySeedResult(inserted, alreadyPresent);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static SqliteCommand CreateInsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dtc_library (
                id, protocol, code, spn, fmi, description, system, severity,
                likely_causes, source_citation, created_at, updated_at)
            VALUES (
                $id, $protocol, $code, $spn, $fmi, $description, $system, $severity,
                $likelyCauses, $sourceCitation, $createdAt, $updatedAt)
            ON CONFLICT(protocol, code) DO NOTHING;
            """;
        return command;
    }

    private static async Task<HashSet<DtcKey>> ReadExistingKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<DtcKey>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT protocol, code FROM dtc_library;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            keys.Add(new DtcKey(reader.GetString(0), reader.GetString(1)));
        return keys;
    }

    private static async Task<IReadOnlyList<DtcSeedRow>> LoadSourceRowsAsync(
        CancellationToken cancellationToken)
    {
        var rows = new List<DtcSeedRow>();
        var sourceKeys = new HashSet<DtcKey>();
        var assembly = typeof(DtcLibrarySeeder).Assembly;

        foreach (var resourceName in ResourceNames)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded DTC resource '{resourceName}' was not found.");
            var jsonRows = await JsonSerializer.DeserializeAsync<List<JsonDtcRow>>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (jsonRows is null || jsonRows.Count == 0)
                throw new InvalidDataException($"Embedded DTC resource '{resourceName}' contains no rows.");

            foreach (var jsonRow in jsonRows)
            {
                var row = ToSeedRow(jsonRow, resourceName);
                var key = new DtcKey(DbEnum.ToCode(row.Protocol), row.Code);
                if (!sourceKeys.Add(key))
                {
                    throw new InvalidDataException(
                        $"Embedded DTC resource '{resourceName}' duplicates key '{key.Protocol}' / '{key.Code}'.");
                }
                rows.Add(row);
            }
        }

        return rows;
    }

    private static DtcSeedRow ToSeedRow(JsonDtcRow row, string resourceName)
    {
        if (!DbEnum.TryParse<ProtocolType>(row.Protocol, out var protocol))
        {
            throw new InvalidDataException(
                $"Embedded DTC resource '{resourceName}' contains unknown protocol '{row.Protocol}'.");
        }
        if (string.IsNullOrWhiteSpace(row.Code))
            throw new InvalidDataException($"Embedded DTC resource '{resourceName}' contains a blank code.");

        return new DtcSeedRow(
            protocol,
            row.Code,
            row.Spn,
            row.Fmi,
            row.Description,
            row.System,
            row.Severity,
            row.LikelyCauses,
            row.SourceCitation);
    }

    private static DtcLibraryEntry ToLibraryEntry(DtcSeedRow row)
    {
        var timestamp = Clock.UtcNow;
        return new DtcLibraryEntry
        {
            Id = Guid.NewGuid().ToString("D"),
            Protocol = row.Protocol,
            Code = row.Code,
            Spn = row.Spn,
            Fmi = row.Fmi,
            Description = row.Description,
            System = row.System,
            Severity = row.Severity,
            LikelyCauses = row.LikelyCauses,
            SourceCitation = row.SourceCitation,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
    }

    private readonly record struct DtcKey(string Protocol, string Code);

    private sealed record DtcSeedRow(
        ProtocolType Protocol,
        string Code,
        int? Spn,
        int? Fmi,
        string? Description,
        string? System,
        string? Severity,
        string? LikelyCauses,
        string? SourceCitation);

    private sealed record JsonDtcRow
    {
        [JsonPropertyName("protocol")] public string? Protocol { get; init; }
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("spn")] public int? Spn { get; init; }
        [JsonPropertyName("fmi")] public int? Fmi { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("severity")] public string? Severity { get; init; }
        [JsonPropertyName("likely_causes")] public string? LikelyCauses { get; init; }
        [JsonPropertyName("source_citation")] public string? SourceCitation { get; init; }
    }
}
