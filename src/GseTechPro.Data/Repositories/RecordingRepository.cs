using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

/// <summary>
/// Persists recording metadata. Raw frames are handled separately by
/// <see cref="RecordingFrameRepository"/>; this repository also converts to the typed runtime
/// <see cref="RecordingSession"/> shape used by capture and playback.
/// </summary>
public sealed class RecordingRepository : SqliteRepository<Recording>
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public RecordingRepository(ISqliteConnectionFactory connectionFactory)
        : base(connectionFactory, "recordings")
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>Creates a recording from its runtime shape and JSON-serializes trigger/PID names.</summary>
    public async Task<RecordingSession> CreateSessionAsync(
        RecordingSession session,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var recording = ToRecording(session, notes);
        await AddAsync(recording, cancellationToken).ConfigureAwait(false);
        return ToSession(recording);
    }

    public async Task<RecordingSession?> GetSessionByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var recording = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return recording is null ? null : ToSession(recording);
    }

    /// <summary>Lists live metadata rows for a work order.</summary>
    public Task<IReadOnlyList<Recording>> ListByWorkOrderAsync(
        string workOrderId,
        CancellationToken cancellationToken = default) =>
        FindAsync("work_order_id", workOrderId, cancellationToken);

    /// <summary>Lists live metadata rows for an asset, across all of its work orders.</summary>
    public Task<IReadOnlyList<Recording>> ListByAssetAsync(
        string assetId,
        CancellationToken cancellationToken = default) =>
        FindAsync("asset_id", assetId, cancellationToken);

    public async Task<IReadOnlyList<RecordingSession>> ListSessionsByWorkOrderAsync(
        string workOrderId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ListByWorkOrderAsync(workOrderId, cancellationToken).ConfigureAwait(false);
        return rows.Select(ToSession).ToArray();
    }

    public async Task<IReadOnlyList<RecordingSession>> ListSessionsByAssetAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ListByAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
        return rows.Select(ToSession).ToArray();
    }

    /// <summary>
    /// Finalizes a recording atomically. Only a still-recording row can transition to
    /// <see cref="RecordingStatus.Completed"/> or <see cref="RecordingStatus.Interrupted"/>;
    /// repeated close attempts return false. The frame count is counted in the same UPDATE so the
    /// metadata agrees with the local frames even when capture was interrupted.
    /// </summary>
    public async Task<bool> CloseRecordingAsync(
        string recordingId,
        RecordingStatus finalStatus,
        string? endedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        if (finalStatus is not RecordingStatus.Completed and not RecordingStatus.Interrupted)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalStatus),
                finalStatus,
                "A recording can only be closed as completed or interrupted.");
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE recordings
            SET ended_at = $endedAt,
                status = $finalStatus,
                frame_count = (
                    SELECT COUNT(*)
                    FROM recording_frames
                    WHERE recording_id = $recordingId
                ),
                updated_at = $updatedAt
            WHERE id = $recordingId
              AND deleted_at IS NULL
              AND status = $recordingStatus;
            """;
        command.Parameters.AddWithValue("$recordingId", recordingId);
        command.Parameters.AddWithValue("$endedAt", endedAt ?? Clock.UtcNow);
        command.Parameters.AddWithValue("$finalStatus", DbEnum.ToCode(finalStatus));
        command.Parameters.AddWithValue("$recordingStatus", DbEnum.ToCode(RecordingStatus.Recording));
        command.Parameters.AddWithValue("$updatedAt", Clock.UtcNow);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Converts a runtime session to its persisted row without touching the database.</summary>
    public static Recording ToRecording(RecordingSession session, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new Recording
        {
            Id = session.Id,
            WorkOrderId = session.WorkOrderId,
            AssetId = session.AssetId,
            TechnicianId = session.TechnicianId,
            Kind = session.Kind,
            Source = session.Source,
            AdapterName = session.AdapterName,
            Protocol = session.Protocol,
            PidNamesJson = RecordingSerialization.SerializePidNames(session.PidNames),
            TriggerJson = RecordingSerialization.SerializeTrigger(session.Trigger),
            StartedAt = session.StartedAt,
            EndedAt = session.EndedAt,
            Status = session.Status,
            FrameCount = session.FrameCount,
            Notes = notes,
        };
    }

    /// <summary>Rehydrates the typed trigger and PID-name list from a persisted metadata row.</summary>
    public static RecordingSession ToSession(Recording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        return new RecordingSession
        {
            Id = recording.Id,
            WorkOrderId = recording.WorkOrderId,
            AssetId = recording.AssetId,
            TechnicianId = recording.TechnicianId,
            Kind = recording.Kind,
            Source = recording.Source,
            AdapterName = recording.AdapterName,
            Protocol = recording.Protocol,
            PidNames = RecordingSerialization.DeserializePidNames(recording.PidNamesJson),
            Trigger = RecordingSerialization.DeserializeTrigger(recording.TriggerJson),
            StartedAt = recording.StartedAt,
            EndedAt = recording.EndedAt,
            Status = recording.Status,
            FrameCount = recording.FrameCount,
        };
    }
}
