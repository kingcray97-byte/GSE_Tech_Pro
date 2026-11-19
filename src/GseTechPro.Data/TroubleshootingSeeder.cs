using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data;

/// <summary>Rows upserted from each embedded troubleshooting resource in one import.</summary>
public sealed record TroubleshootingSeedResult(
    int Models,
    int SafetyWarnings,
    int Symptoms,
    int TestPoints,
    int Trees,
    int Steps)
{
    public int UpsertedCount => Models + SafetyWarnings + Symptoms + TestPoints + Trees + Steps;
}

/// <summary>Validates and imports the offline, authored no-ECU troubleshooting package.</summary>
public interface ITroubleshootingSeeder
{
    Task<TroubleshootingSeedResult> SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Upserts authored troubleshooting content by its stable slug id. Unlike the DTC library, this
/// package deliberately updates matching authored rows on every import so content corrections
/// propagate to existing tablets. Technician-created rows have their own ids and are not touched.
/// All package ids are stored verbatim; seeded content never receives a generated GUID.
/// </summary>
public sealed class TroubleshootingSeeder : ITroubleshootingSeeder
{
    private const string EquipmentModelsResource =
        "GseTechPro.Data.Content.Troubleshooting.equipment_models.json";
    private const string SafetyWarningsResource =
        "GseTechPro.Data.Content.Troubleshooting.safety_warnings.json";
    private const string SymptomsResource =
        "GseTechPro.Data.Content.Troubleshooting.symptoms.json";
    private const string TestPointsResource =
        "GseTechPro.Data.Content.Troubleshooting.test_points.json";
    private const string TreesResource =
        "GseTechPro.Data.Content.Troubleshooting.trees.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ISqliteConnectionFactory _connectionFactory;

    public TroubleshootingSeeder(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Validates the complete package before opening a write transaction, then imports in foreign-key
    /// order: models, warnings, symptoms, test points, trees, and finally their embedded steps.
    /// </summary>
    public async Task<TroubleshootingSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var package = await LoadPackageAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var models = await UpsertModelsAsync(
                connection, transaction, package.Models, cancellationToken).ConfigureAwait(false);
            var safetyWarnings = await UpsertSafetyWarningsAsync(
                connection, transaction, package.SafetyWarnings, cancellationToken).ConfigureAwait(false);
            var symptoms = await UpsertSymptomsAsync(
                connection, transaction, package.Symptoms, cancellationToken).ConfigureAwait(false);
            var testPoints = await UpsertTestPointsAsync(
                connection, transaction, package.TestPoints, cancellationToken).ConfigureAwait(false);
            var trees = await UpsertTreesAsync(
                connection, transaction, package.Trees, cancellationToken).ConfigureAwait(false);
            var steps = await UpsertStepsAsync(
                connection, transaction, package.Steps, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TroubleshootingSeedResult(models, safetyWarnings, symptoms, testPoints, trees, steps);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SeedPackage> LoadPackageAsync(CancellationToken cancellationToken)
    {
        var models = await LoadModelsAsync(cancellationToken).ConfigureAwait(false);
        var safetyWarnings = await LoadSafetyWarningsAsync(cancellationToken).ConfigureAwait(false);
        var symptoms = await LoadSymptomsAsync(cancellationToken).ConfigureAwait(false);

        var modelIds = models.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var safetyWarningIds = safetyWarnings.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var symptomIds = symptoms.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);

        var testPoints = await LoadTestPointsAsync(
            modelIds, safetyWarningIds, cancellationToken).ConfigureAwait(false);
        var (trees, steps) = await LoadTreesAsync(
            modelIds, safetyWarningIds, symptomIds, testPoints, cancellationToken).ConfigureAwait(false);

        return new SeedPackage(models, safetyWarnings, symptoms, testPoints, trees, steps);
    }

    private static async Task<IReadOnlyList<EquipmentModel>> LoadModelsAsync(
        CancellationToken cancellationToken)
    {
        var sourceRows = await ReadJsonRowsAsync<JsonEquipmentModel>(
            EquipmentModelsResource, cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<EquipmentModel>(sourceRows.Count);

        foreach (var source in sourceRows)
        {
            var id = UniqueId(source.Id, ids, EquipmentModelsResource);
            var make = Required(source.Make, EquipmentModelsResource, id, "make");
            var model = Required(source.Model, EquipmentModelsResource, id, "model");
            var category = RequiredEnum<EquipmentCategory>(
                source.Category, EquipmentModelsResource, id, "category");
            var defaultUnitSystem = OptionalEnum<UnitSystem>(
                source.DefaultUnitSystem, EquipmentModelsResource, id, "default_unit_system");

            rows.Add(new EquipmentModel
            {
                Id = id,
                Make = make,
                Model = model,
                Category = category,
                EngineMake = source.EngineMake,
                EngineModel = source.EngineModel,
                Transmission = source.Transmission,
                FuelType = source.FuelType,
                SystemNotes = source.SystemNotes,
                DefaultUnitSystem = defaultUnitSystem,
            });
        }

        return rows;
    }

    private static async Task<IReadOnlyList<SafetyWarning>> LoadSafetyWarningsAsync(
        CancellationToken cancellationToken)
    {
        var sourceRows = await ReadJsonRowsAsync<JsonSafetyWarning>(
            SafetyWarningsResource, cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<SafetyWarning>(sourceRows.Count);

        foreach (var source in sourceRows)
        {
            var id = UniqueId(source.Id, ids, SafetyWarningsResource);
            rows.Add(new SafetyWarning
            {
                Id = id,
                Category = RequiredEnum<SafetyWarningCategory>(
                    source.Category, SafetyWarningsResource, id, "category"),
                Text = Required(source.Text, SafetyWarningsResource, id, "text"),
                SourceCitation = Required(source.SourceCitation, SafetyWarningsResource, id, "source_citation"),
            });
        }

        return rows;
    }

    private static async Task<IReadOnlyList<Symptom>> LoadSymptomsAsync(
        CancellationToken cancellationToken)
    {
        var sourceRows = await ReadJsonRowsAsync<JsonSymptom>(
            SymptomsResource, cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<Symptom>(sourceRows.Count);

        foreach (var source in sourceRows)
        {
            var id = UniqueId(source.Id, ids, SymptomsResource);
            var code = Required(source.Code, SymptomsResource, id, "code");
            if (!codes.Add(code))
            {
                throw new InvalidDataException(
                    $"Embedded troubleshooting resource '{SymptomsResource}' row '{id}' duplicates symptom code '{code}'.");
            }

            rows.Add(new Symptom
            {
                Id = id,
                Code = code,
                Name = Required(source.Name, SymptomsResource, id, "name"),
                Category = RequiredEnum<SymptomCategory>(
                    source.Category, SymptomsResource, id, "category"),
                System = Required(source.System, SymptomsResource, id, "system"),
                Description = Required(source.Description, SymptomsResource, id, "description"),
                IsStandard = true,
            });
        }

        return rows;
    }

    private static async Task<IReadOnlyList<TestPoint>> LoadTestPointsAsync(
        ISet<string> modelIds,
        ISet<string> safetyWarningIds,
        CancellationToken cancellationToken)
    {
        var sourceRows = await ReadJsonRowsAsync<JsonTestPoint>(
            TestPointsResource, cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TestPoint>(sourceRows.Count);

        foreach (var source in sourceRows)
        {
            var id = UniqueId(source.Id, ids, TestPointsResource);
            var equipmentModelId = Required(
                source.EquipmentModelId, TestPointsResource, id, "equipment_model_id");
            RequireReference(
                equipmentModelId, modelIds, TestPointsResource, id,
                "equipment_model_id", EquipmentModelsResource);

            rows.Add(new TestPoint
            {
                Id = id,
                EquipmentModelId = equipmentModelId,
                Name = Required(source.Name, TestPointsResource, id, "name"),
                System = Required(source.System, TestPointsResource, id, "system"),
                Location = Required(source.Location, TestPointsResource, id, "location"),
                ReadingType = RequiredEnum<ReadingType>(
                    source.ReadingType, TestPointsResource, id, "reading_type"),
                ExpectedUnit = Required(source.ExpectedUnit, TestPointsResource, id, "expected_unit"),
                SpecMin = source.SpecMin,
                SpecMax = source.SpecMax,
                SpecSource = Required(source.SpecSource, TestPointsResource, id, "spec_source"),
                ProcedureRef = Required(source.ProcedureRef, TestPointsResource, id, "procedure_ref"),
                SafetyWarningIds = SerializeSafetyWarningIds(
                    source.SafetyWarningIds, safetyWarningIds, TestPointsResource, id, required: true),
            });
        }

        return rows;
    }

    private static async Task<(IReadOnlyList<TroubleshootingTree> Trees, IReadOnlyList<TroubleshootingStep> Steps)>
        LoadTreesAsync(
            ISet<string> modelIds,
            ISet<string> safetyWarningIds,
            ISet<string> symptomIds,
            IReadOnlyList<TestPoint> testPoints,
            CancellationToken cancellationToken)
    {
        var sourceRows = await ReadJsonRowsAsync<JsonTree>(TreesResource, cancellationToken).ConfigureAwait(false);
        var testPointsById = testPoints.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var treeIds = new HashSet<string>(StringComparer.Ordinal);
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var trees = new List<TroubleshootingTree>(sourceRows.Count);
        var steps = new List<TroubleshootingStep>();

        foreach (var source in sourceRows)
        {
            var treeId = UniqueId(source.Id, treeIds, TreesResource);
            var equipmentModelId = Required(
                source.EquipmentModelId, TreesResource, treeId, "equipment_model_id");
            RequireReference(
                equipmentModelId, modelIds, TreesResource, treeId,
                "equipment_model_id", EquipmentModelsResource);
            var symptomId = Required(source.SymptomId, TreesResource, treeId, "symptom_id");
            RequireReference(symptomId, symptomIds, TreesResource, treeId, "symptom_id", SymptomsResource);

            trees.Add(new TroubleshootingTree
            {
                Id = treeId,
                EquipmentModelId = equipmentModelId,
                SymptomId = symptomId,
                Title = Required(source.Title, TreesResource, treeId, "title"),
                System = Required(source.System, TreesResource, treeId, "system"),
                Status = RequiredEnum<TreeStatus>(source.Status, TreesResource, treeId, "status"),
                Source = RequiredEnum<TreeSource>(source.Source, TreesResource, treeId, "source"),
            });

            if (source.Steps is null || source.Steps.Count == 0)
            {
                throw new InvalidDataException(
                    $"Embedded troubleshooting resource '{TreesResource}' tree '{treeId}' requires a non-empty 'steps' array.");
            }

            var currentTreeStepIds = new HashSet<string>(StringComparer.Ordinal);
            var stepOrders = new HashSet<int>();
            foreach (var step in source.Steps)
            {
                var stepId = UniqueId(step.Id, stepIds, TreesResource);
                if (!currentTreeStepIds.Add(stepId))
                {
                    throw new InvalidDataException(
                        $"Embedded troubleshooting resource '{TreesResource}' tree '{treeId}' duplicates step id '{stepId}'.");
                }
                if (step.StepOrder <= 0)
                {
                    throw new InvalidDataException(
                        $"Embedded troubleshooting resource '{TreesResource}' step '{stepId}' requires 'step_order' greater than zero.");
                }
                if (!stepOrders.Add(step.StepOrder))
                {
                    throw new InvalidDataException(
                        $"Embedded troubleshooting resource '{TreesResource}' tree '{treeId}' duplicates step_order '{step.StepOrder}'.");
                }
            }

            foreach (var sourceStep in source.Steps)
            {
                var stepId = Required(sourceStep.Id, TreesResource, treeId, "steps[].id");
                var instruction = Required(sourceStep.Instruction, TreesResource, stepId, "instruction");
                var readingType = OptionalEnum<ReadingType>(
                    sourceStep.ReadingType, TreesResource, stepId, "reading_type");

                TestPoint? testPoint = null;
                if (sourceStep.TestPointId is not null)
                {
                    var testPointId = Required(sourceStep.TestPointId, TreesResource, stepId, "test_point_id");
                    if (!testPointsById.TryGetValue(testPointId, out testPoint))
                    {
                        throw UnresolvedReference(
                            TreesResource, stepId, "test_point_id", testPointId, TestPointsResource);
                    }
                    if (!string.Equals(testPoint.EquipmentModelId, equipmentModelId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Embedded troubleshooting resource '{TreesResource}' step '{stepId}' references test point " +
                            $"'{testPointId}' from equipment model '{testPoint.EquipmentModelId}', but tree '{treeId}' " +
                            $"belongs to '{equipmentModelId}'.");
                    }
                    if (readingType.HasValue && readingType.Value != testPoint.ReadingType)
                    {
                        throw new InvalidDataException(
                            $"Embedded troubleshooting resource '{TreesResource}' step '{stepId}' reading_type " +
                            $"'{DbEnum.ToCode(readingType.Value)}' does not match test point '{testPointId}' " +
                            $"reading_type '{DbEnum.ToCode(testPoint.ReadingType)}'.");
                    }

                    ValidateStepSpecMirror(
                        sourceStep.SpecMin, testPoint.SpecMin, stepId, testPointId, "spec_min");
                    ValidateStepSpecMirror(
                        sourceStep.SpecMax, testPoint.SpecMax, stepId, testPointId, "spec_max");
                    if (sourceStep.SpecSource is not null &&
                        !string.Equals(sourceStep.SpecSource, testPoint.SpecSource, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Embedded troubleshooting resource '{TreesResource}' step '{stepId}' field 'spec_source' " +
                            $"does not match test point '{testPointId}'. A step spec must mirror its test point (ADR-009).");
                    }
                }

                if (testPoint is null && (sourceStep.SpecMin.HasValue || sourceStep.SpecMax.HasValue))
                {
                    throw new InvalidDataException(
                        $"Embedded troubleshooting resource '{TreesResource}' step '{stepId}' has a numeric spec " +
                        "but no test_point_id; steps inherit their spec unit from a test point (ADR-009).");
                }

                ValidateStepTarget(sourceStep.NextOnPass, currentTreeStepIds, stepId, treeId, "next_on_pass");
                ValidateStepTarget(sourceStep.NextOnFail, currentTreeStepIds, stepId, treeId, "next_on_fail");

                steps.Add(new TroubleshootingStep
                {
                    Id = stepId,
                    TreeId = treeId,
                    StepOrder = sourceStep.StepOrder,
                    Instruction = instruction,
                    Question = sourceStep.Question,
                    ReadingType = readingType,
                    TestPointId = testPoint?.Id,
                    SpecMin = sourceStep.SpecMin,
                    SpecMax = sourceStep.SpecMax,
                    SpecSource = sourceStep.SpecSource,
                    SafetyWarningIds = SerializeSafetyWarningIds(
                        sourceStep.SafetyWarningIds, safetyWarningIds, TreesResource, stepId, required: false),
                    NextOnPass = sourceStep.NextOnPass,
                    NextOnFail = sourceStep.NextOnFail,
                    TerminalResult = sourceStep.TerminalResult,
                });
            }
        }

        return (trees, steps);
    }

    private static void ValidateStepSpecMirror(
        double? stepValue,
        double? testPointValue,
        string stepId,
        string testPointId,
        string field)
    {
        if (!stepValue.HasValue || Nullable.Equals(stepValue, testPointValue))
            return;

        throw new InvalidDataException(
            $"Embedded troubleshooting resource '{TreesResource}' step '{stepId}' field '{field}' value " +
            $"'{stepValue.Value}' does not match test point '{testPointId}' value " +
            $"'{testPointValue?.ToString() ?? "null"}'. A step spec must mirror its test point (ADR-009).");
    }

    private static void ValidateStepTarget(
        string? target,
        ISet<string> currentTreeStepIds,
        string stepId,
        string treeId,
        string field)
    {
        if (target is null)
            return;

        var value = Required(target, TreesResource, stepId, field);
        if (!currentTreeStepIds.Contains(value))
        {
            throw new InvalidDataException(
                $"Embedded troubleshooting resource '{TreesResource}' step '{stepId}' field '{field}' references " +
                $"unresolved step '{value}'. Step hops must resolve within tree '{treeId}'.");
        }
    }

    private static string? SerializeSafetyWarningIds(
        IReadOnlyList<string?>? ids,
        ISet<string> knownSafetyWarningIds,
        string resourceName,
        string rowId,
        bool required)
    {
        if (ids is null)
        {
            if (required)
            {
                throw new InvalidDataException(
                    $"Embedded troubleshooting resource '{resourceName}' row '{rowId}' requires 'safety_warning_ids'.");
            }
            return null;
        }

        foreach (var warningId in ids)
        {
            var id = Required(warningId, resourceName, rowId, "safety_warning_ids[]");
            RequireReference(id, knownSafetyWarningIds, resourceName, rowId,
                "safety_warning_ids", SafetyWarningsResource);
        }

        // Compact JSON is the shared DB representation consumed by the guided service.
        return JsonSerializer.Serialize(ids);
    }

    private static async Task<List<T>> ReadJsonRowsAsync<T>(
        string resourceName,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(TroubleshootingSeeder).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded troubleshooting resource '{resourceName}' was not found.");
        var rows = await JsonSerializer.DeserializeAsync<List<T>>(
                stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (rows is null || rows.Count == 0)
            throw new InvalidDataException($"Embedded troubleshooting resource '{resourceName}' contains no rows.");
        return rows;
    }

    private static string UniqueId(string? id, ISet<string> ids, string resourceName)
    {
        var value = Required(id, resourceName, "<unknown>", "id");
        if (!ids.Add(value))
            throw new InvalidDataException($"Embedded troubleshooting resource '{resourceName}' duplicates id '{value}'.");
        return value;
    }

    private static string Required(string? value, string resourceName, string rowId, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Embedded troubleshooting resource '{resourceName}' row '{rowId}' requires a non-blank '{field}'.");
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
                $"Embedded troubleshooting resource '{resourceName}' row '{rowId}' contains unknown " +
                $"{typeof(T).Name} code '{value}' in '{field}'.");
        }
        return parsed;
    }

    private static T? OptionalEnum<T>(string? code, string resourceName, string rowId, string field)
        where T : struct, Enum
    {
        if (code is null)
            return null;
        return RequiredEnum<T>(code, resourceName, rowId, field);
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
            throw UnresolvedReference(resourceName, rowId, field, value, targetResource);
    }

    private static InvalidDataException UnresolvedReference(
        string resourceName,
        string rowId,
        string field,
        string value,
        string targetResource) => new(
        $"Embedded troubleshooting resource '{resourceName}' row '{rowId}' field '{field}' references " +
        $"unresolved id '{value}'. The id must exist in embedded resource '{targetResource}'.");

    private static Task<int> UpsertModelsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<EquipmentModel> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO equipment_models (
                id, make, model, category, engine_make, engine_model, transmission, fuel_type,
                system_notes, default_unit_system, created_at, updated_at, deleted_at)
            VALUES (
                $id, $make, $model, $category, $engineMake, $engineModel, $transmission, $fuelType,
                $systemNotes, $defaultUnitSystem, $createdAt, $updatedAt, $deletedAt)
            ON CONFLICT(id) DO UPDATE SET
                make = excluded.make,
                model = excluded.model,
                category = excluded.category,
                engine_make = excluded.engine_make,
                engine_model = excluded.engine_model,
                transmission = excluded.transmission,
                fuel_type = excluded.fuel_type,
                system_notes = excluded.system_notes,
                default_unit_system = excluded.default_unit_system,
                updated_at = excluded.updated_at,
                deleted_at = NULL;
            """;

        return UpsertRowsAsync(connection, transaction, sql, rows, static (parameters, row) =>
        {
            Add(parameters, "$id", row.Id);
            Add(parameters, "$make", row.Make);
            Add(parameters, "$model", row.Model);
            Add(parameters, "$category", OptionalEnumCode(row.Category));
            Add(parameters, "$engineMake", row.EngineMake);
            Add(parameters, "$engineModel", row.EngineModel);
            Add(parameters, "$transmission", row.Transmission);
            Add(parameters, "$fuelType", row.FuelType);
            Add(parameters, "$systemNotes", row.SystemNotes);
            Add(parameters, "$defaultUnitSystem", OptionalEnumCode(row.DefaultUnitSystem));
            Add(parameters, "$createdAt", row.CreatedAt);
            Add(parameters, "$updatedAt", row.UpdatedAt);
            Add(parameters, "$deletedAt", row.DeletedAt);
        }, cancellationToken);
    }

    private static Task<int> UpsertSafetyWarningsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SafetyWarning> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO safety_warnings (
                id, category, text, source_citation, created_at, updated_at, deleted_at)
            VALUES (
                $id, $category, $text, $sourceCitation, $createdAt, $updatedAt, $deletedAt)
            ON CONFLICT(id) DO UPDATE SET
                category = excluded.category,
                text = excluded.text,
                source_citation = excluded.source_citation,
                updated_at = excluded.updated_at,
                deleted_at = NULL;
            """;

        return UpsertRowsAsync(connection, transaction, sql, rows, static (parameters, row) =>
        {
            Add(parameters, "$id", row.Id);
            Add(parameters, "$category", DbEnum.ToCode(row.Category));
            Add(parameters, "$text", row.Text);
            Add(parameters, "$sourceCitation", row.SourceCitation);
            Add(parameters, "$createdAt", row.CreatedAt);
            Add(parameters, "$updatedAt", row.UpdatedAt);
            Add(parameters, "$deletedAt", row.DeletedAt);
        }, cancellationToken);
    }

    private static Task<int> UpsertSymptomsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Symptom> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO symptoms (
                id, code, name, category, system, description, is_standard, created_at, updated_at)
            VALUES (
                $id, $code, $name, $category, $system, $description, $isStandard, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                code = excluded.code,
                name = excluded.name,
                category = excluded.category,
                system = excluded.system,
                description = excluded.description,
                is_standard = excluded.is_standard,
                updated_at = excluded.updated_at;
            """;

        return UpsertRowsAsync(connection, transaction, sql, rows, static (parameters, row) =>
        {
            Add(parameters, "$id", row.Id);
            Add(parameters, "$code", row.Code);
            Add(parameters, "$name", row.Name);
            Add(parameters, "$category", OptionalEnumCode(row.Category));
            Add(parameters, "$system", row.System);
            Add(parameters, "$description", row.Description);
            Add(parameters, "$isStandard", row.IsStandard ? 1 : 0);
            Add(parameters, "$createdAt", row.CreatedAt);
            Add(parameters, "$updatedAt", row.UpdatedAt);
        }, cancellationToken);
    }

    private static Task<int> UpsertTestPointsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<TestPoint> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO test_points (
                id, equipment_model_id, name, system, location, reading_type, expected_unit,
                spec_min, spec_max, spec_source, procedure_ref, safety_warning_ids,
                created_at, updated_at, deleted_at)
            VALUES (
                $id, $equipmentModelId, $name, $system, $location, $readingType, $expectedUnit,
                $specMin, $specMax, $specSource, $procedureRef, $safetyWarningIds,
                $createdAt, $updatedAt, $deletedAt)
            ON CONFLICT(id) DO UPDATE SET
                equipment_model_id = excluded.equipment_model_id,
                name = excluded.name,
                system = excluded.system,
                location = excluded.location,
                reading_type = excluded.reading_type,
                expected_unit = excluded.expected_unit,
                spec_min = excluded.spec_min,
                spec_max = excluded.spec_max,
                spec_source = excluded.spec_source,
                procedure_ref = excluded.procedure_ref,
                safety_warning_ids = excluded.safety_warning_ids,
                updated_at = excluded.updated_at,
                deleted_at = NULL;
            """;

        return UpsertRowsAsync(connection, transaction, sql, rows, static (parameters, row) =>
        {
            Add(parameters, "$id", row.Id);
            Add(parameters, "$equipmentModelId", row.EquipmentModelId);
            Add(parameters, "$name", row.Name);
            Add(parameters, "$system", row.System);
            Add(parameters, "$location", row.Location);
            Add(parameters, "$readingType", DbEnum.ToCode(row.ReadingType));
            Add(parameters, "$expectedUnit", row.ExpectedUnit);
            Add(parameters, "$specMin", row.SpecMin);
            Add(parameters, "$specMax", row.SpecMax);
            Add(parameters, "$specSource", row.SpecSource);
            Add(parameters, "$procedureRef", row.ProcedureRef);
            Add(parameters, "$safetyWarningIds", row.SafetyWarningIds);
            Add(parameters, "$createdAt", row.CreatedAt);
            Add(parameters, "$updatedAt", row.UpdatedAt);
            Add(parameters, "$deletedAt", row.DeletedAt);
        }, cancellationToken);
    }

    private static Task<int> UpsertTreesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<TroubleshootingTree> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO troubleshooting_trees (
                id, equipment_model_id, symptom_id, title, system, status, source, created_at, updated_at)
            VALUES (
                $id, $equipmentModelId, $symptomId, $title, $system, $status, $source, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                equipment_model_id = excluded.equipment_model_id,
                symptom_id = excluded.symptom_id,
                title = excluded.title,
                system = excluded.system,
                status = CASE
                    WHEN troubleshooting_trees.status = 'approved' AND excluded.status = 'draft'
                    THEN troubleshooting_trees.status
                    ELSE excluded.status
                END,
                source = excluded.source,
                updated_at = CASE
                    WHEN troubleshooting_trees.status = 'approved' AND excluded.status = 'draft'
                    THEN troubleshooting_trees.updated_at
                    ELSE excluded.updated_at
                END;
            """;

        return UpsertRowsAsync(connection, transaction, sql, rows, static (parameters, row) =>
        {
            Add(parameters, "$id", row.Id);
            Add(parameters, "$equipmentModelId", row.EquipmentModelId);
            Add(parameters, "$symptomId", row.SymptomId);
            Add(parameters, "$title", row.Title);
            Add(parameters, "$system", row.System);
            Add(parameters, "$status", DbEnum.ToCode(row.Status));
            Add(parameters, "$source", DbEnum.ToCode(row.Source));
            Add(parameters, "$createdAt", row.CreatedAt);
            Add(parameters, "$updatedAt", row.UpdatedAt);
        }, cancellationToken);
    }

    private static Task<int> UpsertStepsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<TroubleshootingStep> rows,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO troubleshooting_steps (
                id, tree_id, step_order, instruction, question, reading_type, test_point_id,
                spec_min, spec_max, spec_source, safety_warning_ids, next_on_pass, next_on_fail,
                terminal_result, created_at, updated_at)
            VALUES (
                $id, $treeId, $stepOrder, $instruction, $question, $readingType, $testPointId,
                $specMin, $specMax, $specSource, $safetyWarningIds, $nextOnPass, $nextOnFail,
                $terminalResult, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                tree_id = excluded.tree_id,
                step_order = excluded.step_order,
                instruction = excluded.instruction,
                question = excluded.question,
                reading_type = excluded.reading_type,
                test_point_id = excluded.test_point_id,
                spec_min = excluded.spec_min,
                spec_max = excluded.spec_max,
                spec_source = excluded.spec_source,
                safety_warning_ids = excluded.safety_warning_ids,
                next_on_pass = excluded.next_on_pass,
                next_on_fail = excluded.next_on_fail,
                terminal_result = excluded.terminal_result,
                updated_at = excluded.updated_at;
            """;

        return UpsertRowsAsync(connection, transaction, sql, rows, static (parameters, row) =>
        {
            Add(parameters, "$id", row.Id);
            Add(parameters, "$treeId", row.TreeId);
            Add(parameters, "$stepOrder", row.StepOrder);
            Add(parameters, "$instruction", row.Instruction);
            Add(parameters, "$question", row.Question);
            Add(parameters, "$readingType", OptionalEnumCode(row.ReadingType));
            Add(parameters, "$testPointId", row.TestPointId);
            Add(parameters, "$specMin", row.SpecMin);
            Add(parameters, "$specMax", row.SpecMax);
            Add(parameters, "$specSource", row.SpecSource);
            Add(parameters, "$safetyWarningIds", row.SafetyWarningIds);
            Add(parameters, "$nextOnPass", row.NextOnPass);
            Add(parameters, "$nextOnFail", row.NextOnFail);
            Add(parameters, "$terminalResult", row.TerminalResult);
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

    private static string? OptionalEnumCode<T>(T? value) where T : struct, Enum =>
        value.HasValue ? DbEnum.ToCode(value.Value) : null;

    private sealed record SeedPackage(
        IReadOnlyList<EquipmentModel> Models,
        IReadOnlyList<SafetyWarning> SafetyWarnings,
        IReadOnlyList<Symptom> Symptoms,
        IReadOnlyList<TestPoint> TestPoints,
        IReadOnlyList<TroubleshootingTree> Trees,
        IReadOnlyList<TroubleshootingStep> Steps);

    private sealed class JsonEquipmentModel
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("make")] public string? Make { get; init; }
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("category")] public string? Category { get; init; }
        [JsonPropertyName("engine_make")] public string? EngineMake { get; init; }
        [JsonPropertyName("engine_model")] public string? EngineModel { get; init; }
        [JsonPropertyName("transmission")] public string? Transmission { get; init; }
        [JsonPropertyName("fuel_type")] public string? FuelType { get; init; }
        [JsonPropertyName("system_notes")] public string? SystemNotes { get; init; }
        [JsonPropertyName("default_unit_system")] public string? DefaultUnitSystem { get; init; }
    }

    private sealed class JsonSafetyWarning
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("category")] public string? Category { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("source_citation")] public string? SourceCitation { get; init; }
    }

    private sealed class JsonSymptom
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("code")] public string? Code { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("category")] public string? Category { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    private sealed class JsonTestPoint
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("equipment_model_id")] public string? EquipmentModelId { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("location")] public string? Location { get; init; }
        [JsonPropertyName("reading_type")] public string? ReadingType { get; init; }
        [JsonPropertyName("expected_unit")] public string? ExpectedUnit { get; init; }
        [JsonPropertyName("spec_min")] public double? SpecMin { get; init; }
        [JsonPropertyName("spec_max")] public double? SpecMax { get; init; }
        [JsonPropertyName("spec_source")] public string? SpecSource { get; init; }
        [JsonPropertyName("procedure_ref")] public string? ProcedureRef { get; init; }
        [JsonPropertyName("safety_warning_ids")] public List<string?>? SafetyWarningIds { get; init; }
    }

    private sealed class JsonTree
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("equipment_model_id")] public string? EquipmentModelId { get; init; }
        [JsonPropertyName("symptom_id")] public string? SymptomId { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("source")] public string? Source { get; init; }
        [JsonPropertyName("steps")] public List<JsonTroubleshootingStep>? Steps { get; init; }
    }

    private sealed class JsonTroubleshootingStep
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("step_order")] public int StepOrder { get; init; }
        [JsonPropertyName("instruction")] public string? Instruction { get; init; }
        [JsonPropertyName("question")] public string? Question { get; init; }
        [JsonPropertyName("reading_type")] public string? ReadingType { get; init; }
        [JsonPropertyName("test_point_id")] public string? TestPointId { get; init; }
        [JsonPropertyName("spec_min")] public double? SpecMin { get; init; }
        [JsonPropertyName("spec_max")] public double? SpecMax { get; init; }
        [JsonPropertyName("spec_source")] public string? SpecSource { get; init; }
        [JsonPropertyName("safety_warning_ids")] public List<string?>? SafetyWarningIds { get; init; }
        [JsonPropertyName("next_on_pass")] public string? NextOnPass { get; init; }
        [JsonPropertyName("next_on_fail")] public string? NextOnFail { get; init; }
        [JsonPropertyName("terminal_result")] public string? TerminalResult { get; init; }
    }
}
