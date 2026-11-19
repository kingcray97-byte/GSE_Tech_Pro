using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data;

/// <summary>Scope content rows upserted in one import.</summary>
public sealed record ScopeSeedResult(int GuidedTests, int ReferenceWaveforms)
{
    public int UpsertedCount => GuidedTests + ReferenceWaveforms;
}

/// <summary>Validates and imports the authored guided scope tests and reference expectations.</summary>
public interface IScopeSeeder
{
    Task<ScopeSeedResult> SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Imports scope content by stable slug id. The complete package and its database references are
/// validated before a write transaction opens; references are upserted before the guided tests that
/// point to them. Authored rows are corrected on reseed and unrelated technician-created rows are untouched.
/// </summary>
public sealed class ScopeSeeder : IScopeSeeder
{
    private const string ReferenceWaveformsResource =
        "GseTechPro.Data.Content.Troubleshooting.reference_waveforms.json";
    private const string GuidedTestsResource =
        "GseTechPro.Data.Content.Troubleshooting.guided_tests.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ISqliteConnectionFactory _connectionFactory;

    public ScopeSeeder(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<ScopeSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var package = await LoadPackageAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // FK order is significant: guided_tests.reference_waveform_id resolves to this table.
            var referenceWaveforms = await UpsertReferenceWaveformsAsync(
                connection, transaction, package.ReferenceWaveforms, cancellationToken).ConfigureAwait(false);
            var guidedTests = await UpsertGuidedTestsAsync(
                connection, transaction, package.GuidedTests, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ScopeSeedResult(guidedTests, referenceWaveforms);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SeedPackage> LoadPackageAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var referenceSources = await ReadJsonRowsAsync<JsonReferenceWaveform>(
            ReferenceWaveformsResource, cancellationToken).ConfigureAwait(false);
        var guidedSources = await ReadJsonRowsAsync<JsonGuidedTest>(
            GuidedTestsResource, cancellationToken).ConfigureAwait(false);

        // Scope safety warnings are JSON ids rather than SQL FKs, so validate every one explicitly.
        // Model ids are also checked here to fail with a useful content error before any writes.
        var equipmentModelIds = await LoadEquipmentModelIdsAsync(connection, cancellationToken).ConfigureAwait(false);
        var safetyWarningIds = await LoadSafetyWarningIdsAsync(connection, cancellationToken).ConfigureAwait(false);

        var referenceWaveforms = LoadReferenceWaveforms(referenceSources, equipmentModelIds);
        var referenceWaveformIds = referenceWaveforms.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var guidedTests = LoadGuidedTests(guidedSources, equipmentModelIds, safetyWarningIds, referenceWaveformIds);
        return new SeedPackage(guidedTests, referenceWaveforms);
    }

    private static IReadOnlyList<ReferenceWaveform> LoadReferenceWaveforms(
        IEnumerable<JsonReferenceWaveform> sourceRows,
        ISet<string> equipmentModelIds)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ReferenceWaveform>();

        foreach (var source in sourceRows)
        {
            var id = UniqueId(source.Id, ids, ReferenceWaveformsResource);
            var equipmentModelId = OptionalModelReference(
                source.EquipmentModelId, equipmentModelIds, ReferenceWaveformsResource, id);

            rows.Add(new ReferenceWaveform
            {
                Id = id,
                Name = Required(source.Name, ReferenceWaveformsResource, id, "name"),
                EquipmentModelId = equipmentModelId,
                System = source.System,
                TestName = source.TestName,
                ExpectedJson = CanonicalizeExpectedJson(
                    source.ExpectedJson, ReferenceWaveformsResource, id),
                DataPath = source.DataPath ?? "",
                Source = RequiredEnum<WaveformSource>(
                    source.Source, ReferenceWaveformsResource, id, "source"),
                Citation = source.Citation,
            });
        }

        return rows;
    }

    private static IReadOnlyList<GuidedTest> LoadGuidedTests(
        IEnumerable<JsonGuidedTest> sourceRows,
        ISet<string> equipmentModelIds,
        ISet<string> safetyWarningIds,
        ISet<string> referenceWaveformIds)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<GuidedTest>();

        foreach (var source in sourceRows)
        {
            var id = UniqueId(source.Id, ids, GuidedTestsResource);
            var equipmentModelId = OptionalModelReference(
                source.EquipmentModelId, equipmentModelIds, GuidedTestsResource, id);
            string? referenceWaveformId = null;
            if (source.ReferenceWaveformId is not null)
            {
                referenceWaveformId = Required(
                    source.ReferenceWaveformId, GuidedTestsResource, id, "reference_waveform_id");
                RequireReference(
                    referenceWaveformId, referenceWaveformIds, GuidedTestsResource, id,
                    "reference_waveform_id", ReferenceWaveformsResource);
            }

            rows.Add(new GuidedTest
            {
                Id = id,
                Name = Required(source.Name, GuidedTestsResource, id, "name"),
                EquipmentModelId = equipmentModelId,
                System = source.System,
                Description = source.Description,
                Procedure = source.Procedure,
                SafetyWarningIds = SerializeSafetyWarningIds(
                    source.SafetyWarningIds, safetyWarningIds, GuidedTestsResource, id),
                ReferenceWaveformId = referenceWaveformId,
                ScopeSetupJson = CanonicalizeCaptureRequest(
                    source.ScopeSetupJson, GuidedTestsResource, id),
            });
        }

        return rows;
    }

    private static string? CanonicalizeExpectedJson(
        JsonElement? json,
        string resourceName,
        string rowId)
    {
        if (json is null || json.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        try
        {
            var expectations = ScopeJson.DeserializeReferenceExpectations(json.Value.GetRawText());
            if (expectations is null)
                return null;
            ValidateExpectations(expectations, resourceName, rowId);
            return ScopeJson.SerializeReferenceExpectations(expectations);
        }
        catch (JsonException ex)
        {
            throw InvalidJson(resourceName, rowId, "expected_json", ex);
        }
    }

    private static void ValidateExpectations(
        ReferenceExpectations expectations,
        string resourceName,
        string rowId)
    {
        if (expectations.Measurements is null)
        {
            throw new InvalidDataException(
                $"Embedded scope resource '{resourceName}' row '{rowId}' requires 'expected_json.measurements' to be an array.");
        }

        for (var i = 0; i < expectations.Measurements.Count; i++)
        {
            var expectation = expectations.Measurements[i];
            if (expectation is null)
            {
                throw new InvalidDataException(
                    $"Embedded scope resource '{resourceName}' row '{rowId}' contains a null measurement at index {i}.");
            }
            if (expectation.Channel <= 0)
            {
                throw new InvalidDataException(
                    $"Embedded scope resource '{resourceName}' row '{rowId}' measurement {i} requires a 1-based 'channel'.");
            }

            var hasNominal = expectation.Nominal.HasValue || expectation.Tolerance.HasValue;
            var hasRange = expectation.Min.HasValue || expectation.Max.HasValue;
            var completeNominal = expectation.Nominal.HasValue && expectation.Tolerance.HasValue;
            var completeRange = expectation.Min.HasValue && expectation.Max.HasValue;

            if (expectation.IsTodo)
            {
                if (hasNominal || hasRange)
                    throw InvalidExpectationForm(resourceName, rowId, i);
                continue;
            }

            // Exactly one complete numeric form is required. This rejects min-without-max,
            // nominal-without-tolerance, nominal mixed with min/max, and a non-TODO empty expectation.
            if ((completeNominal && !hasRange) || (completeRange && !hasNominal))
            {
                if (completeNominal && expectation.Tolerance < 0)
                {
                    throw new InvalidDataException(
                        $"Embedded scope resource '{resourceName}' row '{rowId}' measurement {i} requires a non-negative 'tolerance'.");
                }
                if (completeRange && expectation.Min > expectation.Max)
                {
                    throw new InvalidDataException(
                        $"Embedded scope resource '{resourceName}' row '{rowId}' measurement {i} has 'min' greater than 'max'.");
                }
                continue;
            }

            throw InvalidExpectationForm(resourceName, rowId, i);
        }
    }

    private static InvalidDataException InvalidExpectationForm(
        string resourceName,
        string rowId,
        int index) => new(
        $"Embedded scope resource '{resourceName}' row '{rowId}' measurement {index} must use exactly one of " +
        "'nominal'+'tolerance', 'min'+'max', or 'is_todo'=true without numeric bounds (ADR-011 §5).");

    private static string? CanonicalizeCaptureRequest(
        JsonElement? json,
        string resourceName,
        string rowId)
    {
        if (json is null || json.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        try
        {
            var request = ScopeJson.DeserializeCaptureRequest(json.Value.GetRawText());
            if (request is null)
                return null;
            ValidateCaptureRequest(request, resourceName, rowId);
            return ScopeJson.SerializeCaptureRequest(request);
        }
        catch (JsonException ex)
        {
            throw InvalidJson(resourceName, rowId, "scope_setup_json", ex);
        }
    }

    private static void ValidateCaptureRequest(
        ScopeCaptureRequest request,
        string resourceName,
        string rowId)
    {
        if (request.Channels is null || request.Channels.Count == 0)
        {
            throw new InvalidDataException(
                $"Embedded scope resource '{resourceName}' row '{rowId}' requires at least one scope channel.");
        }
        if (request.SampleRateHz < 1 || request.TimebaseSecondsPerDivision <= 0)
        {
            throw new InvalidDataException(
                $"Embedded scope resource '{resourceName}' row '{rowId}' requires a positive sample rate and timebase.");
        }
        if (request.SampleCount is <= 0)
        {
            throw new InvalidDataException(
                $"Embedded scope resource '{resourceName}' row '{rowId}' requires a positive sample_count when supplied.");
        }

        var channels = new HashSet<int>();
        foreach (var channel in request.Channels)
        {
            if (channel is null || channel.Channel <= 0 || !channels.Add(channel.Channel))
            {
                throw new InvalidDataException(
                    $"Embedded scope resource '{resourceName}' row '{rowId}' requires unique, 1-based scope channels.");
            }
        }

        if (request.TriggerMode != ScopeTriggerMode.None &&
            (request.TriggerChannel <= 0 || !channels.Contains(request.TriggerChannel)))
        {
            throw new InvalidDataException(
                $"Embedded scope resource '{resourceName}' row '{rowId}' trigger_channel must select an enabled channel.");
        }
    }

    private static InvalidDataException InvalidJson(
        string resourceName,
        string rowId,
        string field,
        JsonException inner) => new(
        $"Embedded scope resource '{resourceName}' row '{rowId}' contains invalid '{field}'.", inner);

    private static string? OptionalModelReference(
        string? equipmentModelId,
        ISet<string> knownIds,
        string resourceName,
        string rowId)
    {
        if (equipmentModelId is null)
            return null;

        var value = Required(equipmentModelId, resourceName, rowId, "equipment_model_id");
        RequireReference(value, knownIds, resourceName, rowId, "equipment_model_id", "equipment_models.id");
        return value;
    }

    private static string? SerializeSafetyWarningIds(
        IReadOnlyList<string?>? ids,
        ISet<string> knownSafetyWarningIds,
        string resourceName,
        string rowId)
    {
        if (ids is null)
            return null;

        foreach (var warningId in ids)
        {
            var id = Required(warningId, resourceName, rowId, "safety_warning_ids[]");
            RequireReference(id, knownSafetyWarningIds, resourceName, rowId,
                "safety_warning_ids", "safety_warnings.id");
        }

        return JsonSerializer.Serialize(ids);
    }

    private static async Task<HashSet<string>> LoadEquipmentModelIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM equipment_models WHERE deleted_at IS NULL;";
        return await ReadIdsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> LoadSafetyWarningIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM safety_warnings WHERE deleted_at IS NULL;";
        return await ReadIdsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<string>> ReadIdsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task<List<T>> ReadJsonRowsAsync<T>(
        string resourceName,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(ScopeSeeder).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded scope resource '{resourceName}' was not found.");
        var rows = await JsonSerializer.DeserializeAsync<List<T>>(
                stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (rows is null || rows.Count == 0)
            throw new InvalidDataException($"Embedded scope resource '{resourceName}' contains no rows.");
        return rows;
    }

    private static string UniqueId(string? id, ISet<string> ids, string resourceName)
    {
        var value = Required(id, resourceName, "<unknown>", "id");
        if (!ids.Add(value))
            throw new InvalidDataException($"Embedded scope resource '{resourceName}' duplicates id '{value}'.");
        return value;
    }

    private static string Required(string? value, string resourceName, string rowId, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Embedded scope resource '{resourceName}' row '{rowId}' requires a non-blank '{field}'.");
        }
        return value;
    }

    private static T RequiredEnum<T>(string? code, string resourceName, string rowId, string field)
        where T : struct, Enum
    {
        var value = Required(code, resourceName, rowId, field);
        if (!DbEnum.TryParse<T>(value, out var parsed))
        {
            throw new InvalidDataException(
                $"Embedded scope resource '{resourceName}' row '{rowId}' contains unknown " +
                $"{typeof(T).Name} code '{value}' in '{field}'.");
        }
        return parsed;
    }

    private static void RequireReference(
        string value,
        ISet<string> knownIds,
        string resourceName,
        string rowId,
        string field,
        string targetResource)
    {
        if (!knownIds.Contains(value))
        {
            throw new InvalidDataException(
                $"Embedded scope resource '{resourceName}' row '{rowId}' field '{field}' references " +
                $"unresolved id '{value}'. The id must exist in '{targetResource}'.");
        }
    }

    private static Task<int> UpsertReferenceWaveformsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ReferenceWaveform> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO reference_waveforms (
                id, name, equipment_model_id, system, test_name, expected_json, data_path,
                source, citation, created_at, updated_at)
            VALUES (
                $id, $name, $equipmentModelId, $system, $testName, $expectedJson, $dataPath,
                $source, $citation, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                equipment_model_id = excluded.equipment_model_id,
                system = excluded.system,
                test_name = excluded.test_name,
                expected_json = excluded.expected_json,
                data_path = excluded.data_path,
                source = excluded.source,
                citation = excluded.citation,
                updated_at = excluded.updated_at;
            """;

        return UpsertRowsAsync(connection, transaction, sql, rows, static (parameters, row) =>
        {
            Add(parameters, "$id", row.Id);
            Add(parameters, "$name", row.Name);
            Add(parameters, "$equipmentModelId", row.EquipmentModelId);
            Add(parameters, "$system", row.System);
            Add(parameters, "$testName", row.TestName);
            Add(parameters, "$expectedJson", row.ExpectedJson);
            Add(parameters, "$dataPath", string.IsNullOrEmpty(row.DataPath) ? null : row.DataPath);
            Add(parameters, "$source", DbEnum.ToCode(row.Source));
            Add(parameters, "$citation", row.Citation);
            Add(parameters, "$createdAt", row.CreatedAt);
            Add(parameters, "$updatedAt", row.UpdatedAt);
        }, cancellationToken);
    }

    private static Task<int> UpsertGuidedTestsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<GuidedTest> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO guided_tests (
                id, name, equipment_model_id, system, description, procedure, safety_warning_ids,
                reference_waveform_id, scope_setup_json, created_at, updated_at)
            VALUES (
                $id, $name, $equipmentModelId, $system, $description, $procedure, $safetyWarningIds,
                $referenceWaveformId, $scopeSetupJson, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                equipment_model_id = excluded.equipment_model_id,
                system = excluded.system,
                description = excluded.description,
                procedure = excluded.procedure,
                safety_warning_ids = excluded.safety_warning_ids,
                reference_waveform_id = excluded.reference_waveform_id,
                scope_setup_json = excluded.scope_setup_json,
                updated_at = excluded.updated_at;
            """;

        return UpsertRowsAsync(connection, transaction, sql, rows, static (parameters, row) =>
        {
            Add(parameters, "$id", row.Id);
            Add(parameters, "$name", row.Name);
            Add(parameters, "$equipmentModelId", row.EquipmentModelId);
            Add(parameters, "$system", row.System);
            Add(parameters, "$description", row.Description);
            Add(parameters, "$procedure", row.Procedure);
            Add(parameters, "$safetyWarningIds", row.SafetyWarningIds);
            Add(parameters, "$referenceWaveformId", row.ReferenceWaveformId);
            Add(parameters, "$scopeSetupJson", row.ScopeSetupJson);
            Add(parameters, "$createdAt", row.CreatedAt);
            Add(parameters, "$updatedAt", row.UpdatedAt);
        }, cancellationToken);
    }

    private static async Task<int> UpsertRowsAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IEnumerable<T> rows,
        Action<SqliteParameterCollection, T> bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var affected = 0;

        foreach (var row in rows)
        {
            command.Parameters.Clear();
            bind(command.Parameters, row);
            affected += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return affected;
    }

    private static void Add(SqliteParameterCollection parameters, string name, object? value) =>
        parameters.AddWithValue(name, value ?? DBNull.Value);

    private sealed record SeedPackage(
        IReadOnlyList<GuidedTest> GuidedTests,
        IReadOnlyList<ReferenceWaveform> ReferenceWaveforms);

    private sealed class JsonReferenceWaveform
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("equipment_model_id")] public string? EquipmentModelId { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("test_name")] public string? TestName { get; init; }
        [JsonPropertyName("expected_json")] public JsonElement? ExpectedJson { get; init; }
        [JsonPropertyName("data_path")] public string? DataPath { get; init; }
        [JsonPropertyName("source")] public string? Source { get; init; }
        [JsonPropertyName("citation")] public string? Citation { get; init; }
    }

    private sealed class JsonGuidedTest
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("equipment_model_id")] public string? EquipmentModelId { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("procedure")] public string? Procedure { get; init; }
        [JsonPropertyName("safety_warning_ids")] public List<string?>? SafetyWarningIds { get; init; }
        [JsonPropertyName("reference_waveform_id")] public string? ReferenceWaveformId { get; init; }
        [JsonPropertyName("scope_setup_json")] public JsonElement? ScopeSetupJson { get; init; }
    }
}
