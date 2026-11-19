using GseTechPro.Core.Models;

namespace GseTechPro.Data.Repositories;

/// <summary>A guided test and its optional linked reference waveform.</summary>
public sealed record GuidedTestWithReference(
    GuidedTest GuidedTest,
    ReferenceWaveform? ReferenceWaveform);

public interface IGuidedTestRepository : IRepository<GuidedTest>
{
    Task<IReadOnlyList<GuidedTest>> GetByModelAsync(
        string? equipmentModelId,
        CancellationToken cancellationToken = default);

    Task<GuidedTestWithReference?> GetByIdWithReferenceAsync(
        string id,
        CancellationToken cancellationToken = default);
}

public interface IReferenceWaveformRepository : IRepository<ReferenceWaveform>
{
    Task<IReadOnlyList<ReferenceWaveform>> GetByModelAsync(
        string? equipmentModelId,
        CancellationToken cancellationToken = default);
}

public interface IWaveformRepository : IRepository<Waveform>
{
    Task InsertAsync(Waveform waveform, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Waveform>> GetByWorkOrderAsync(
        string workOrderId,
        CancellationToken cancellationToken = default);

    Task<bool> SoftDeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> UpdateMeasurementsAsync(
        string id,
        string? measurementsJson,
        CancellationToken cancellationToken = default);
}

/// <summary>Guided scope recipes. Enum/json values use the shared Core and repository mappings.</summary>
public sealed class GuidedTestRepository : SqliteRepository<GuidedTest>, IGuidedTestRepository
{
    private readonly ReferenceWaveformRepository _referenceWaveforms;

    public GuidedTestRepository(ISqliteConnectionFactory connectionFactory)
        : base(connectionFactory, "guided_tests")
    {
        _referenceWaveforms = new ReferenceWaveformRepository(connectionFactory);
    }

    public Task<IReadOnlyList<GuidedTest>> GetByModelAsync(
        string? equipmentModelId,
        CancellationToken cancellationToken = default) =>
        FindAsync(nameof(GuidedTest.EquipmentModelId), equipmentModelId, cancellationToken);

    public async Task<GuidedTestWithReference?> GetByIdWithReferenceAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var guidedTest = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (guidedTest is null)
            return null;

        var reference = guidedTest.ReferenceWaveformId is null
            ? null
            : await _referenceWaveforms.GetByIdAsync(
                guidedTest.ReferenceWaveformId, cancellationToken).ConfigureAwait(false);
        return new GuidedTestWithReference(guidedTest, reference);
    }
}

/// <summary>Authored reference expectations and optional known-good sample paths.</summary>
public sealed class ReferenceWaveformRepository(ISqliteConnectionFactory connectionFactory)
    : SqliteRepository<ReferenceWaveform>(connectionFactory, "reference_waveforms"),
        IReferenceWaveformRepository
{
    public new async Task<ReferenceWaveform?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        Normalize(await base.GetByIdAsync(id, cancellationToken).ConfigureAwait(false));

    public new async Task<IReadOnlyList<ReferenceWaveform>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        Normalize(await base.GetAllAsync(cancellationToken).ConfigureAwait(false));

    public new async Task<IReadOnlyList<ReferenceWaveform>> FindAsync(
        string column,
        object? value,
        CancellationToken cancellationToken = default) =>
        Normalize(await base.FindAsync(column, value, cancellationToken).ConfigureAwait(false));

    public Task<IReadOnlyList<ReferenceWaveform>> GetByModelAsync(
        string? equipmentModelId,
        CancellationToken cancellationToken = default) =>
        FindAsync(nameof(ReferenceWaveform.EquipmentModelId), equipmentModelId, cancellationToken);

    private static ReferenceWaveform? Normalize(ReferenceWaveform? row) =>
        row is not null && row.DataPath is null ? row with { DataPath = "" } : row;

    private static IReadOnlyList<ReferenceWaveform> Normalize(IEnumerable<ReferenceWaveform> rows) =>
        rows.Select(row => row.DataPath is null ? row with { DataPath = "" } : row).ToArray();
}

/// <summary>Saved capture metadata. Binary samples remain in the .wfm file named by DataPath.</summary>
public sealed class WaveformRepository : SqliteRepository<Waveform>, IWaveformRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public WaveformRepository(ISqliteConnectionFactory connectionFactory)
        : base(connectionFactory, "waveforms")
    {
        _connectionFactory = connectionFactory;
    }

    public Task InsertAsync(Waveform waveform, CancellationToken cancellationToken = default) =>
        AddAsync(waveform, cancellationToken);

    public Task<IReadOnlyList<Waveform>> GetByWorkOrderAsync(
        string workOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workOrderId);
        return FindAsync(nameof(Waveform.WorkOrderId), workOrderId, cancellationToken);
    }

    public Task<bool> SoftDeleteAsync(string id, CancellationToken cancellationToken = default) =>
        DeleteAsync(id, cancellationToken);

    public async Task<bool> UpdateMeasurementsAsync(
        string id,
        string? measurementsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE waveforms
            SET measurements_json = $measurementsJson
            WHERE id = $id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithNullableValue("$measurementsJson", measurementsJson);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0;
    }
}
