using GseTechPro.Core.Contracts;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

/// <summary>
/// High-volume recording-frame storage. Frames use SQLite rowids for locality and retain a separate
/// GUID for export/uniqueness. Writes are transactional batch appends; playback reads are bounded
/// sequence windows rather than unbounded CRUD scans.
/// </summary>
public sealed class RecordingFrameRepository
{
    private const string SelectColumns = "id, guid, recording_id, sequence, captured_at, pids_json, created_at";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public RecordingFrameRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Gets one persisted frame by its SQLite INTEGER rowid.</summary>
    public Task<RecordingFrame?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
        GetOneAsync("id", id, cancellationToken);

    /// <summary>Gets one persisted frame by its separate GUID.</summary>
    public Task<RecordingFrame?> GetByGuidAsync(string guid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guid);
        return GetOneAsync("guid", guid, cancellationToken);
    }

    /// <summary>
    /// Appends persisted frames in one transaction using one prepared INSERT. The database assigns
    /// each rowid; <see cref="RecordingFrame.Id"/> on input objects is therefore intentionally ignored.
    /// </summary>
    public async Task<int> AppendAsync(
        IEnumerable<RecordingFrame> frames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var appended = 0;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO recording_frames
                    (guid, recording_id, sequence, captured_at, pids_json, created_at)
                VALUES
                    ($guid, $recordingId, $sequence, $capturedAt, $pidsJson, $createdAt);
                """;

            var guid = command.Parameters.Add("$guid", SqliteType.Text);
            var recordingId = command.Parameters.Add("$recordingId", SqliteType.Text);
            var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
            var capturedAt = command.Parameters.Add("$capturedAt", SqliteType.Text);
            var pidsJson = command.Parameters.Add("$pidsJson", SqliteType.Text);
            var createdAt = command.Parameters.Add("$createdAt", SqliteType.Text);

            foreach (var frame in frames)
            {
                if (frame is null)
                    throw new ArgumentException("The frame batch cannot contain a null frame.", nameof(frames));
                if (string.IsNullOrWhiteSpace(frame.Guid))
                    throw new ArgumentException("Every frame requires a GUID.", nameof(frames));
                if (string.IsNullOrWhiteSpace(frame.RecordingId))
                    throw new ArgumentException("Every frame requires a recording id.", nameof(frames));
                if (frame.Sequence < 0)
                    throw new ArgumentOutOfRangeException(nameof(frames), "Frame sequences are zero-based and cannot be negative.");

                guid.Value = frame.Guid;
                recordingId.Value = frame.RecordingId;
                sequence.Value = frame.Sequence;
                capturedAt.Value = frame.CapturedAt;
                pidsJson.Value = frame.PidsJson;
                createdAt.Value = frame.CreatedAt;

                appended += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return appended;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Converts and appends runtime batches, faithfully persisting every PID's IsSimulated flag.</summary>
    public Task<int> AppendAsync(
        string recordingId,
        IEnumerable<RecordedFrameBatch> frames,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        ArgumentNullException.ThrowIfNull(frames);
        return AppendAsync(frames.Select(frame => ToFrame(recordingId, frame)), cancellationToken);
    }

    /// <summary>
    /// Fetches at most <see cref="RecordingSeekRequest.MaxFrames"/> frames beginning at the requested
    /// sequence (inclusive). One extra row is read as look-ahead so <c>EndOfRecording</c> is exact.
    /// <see cref="RecordingFrameWindow.StartSequence"/> echoes the requested seek position.
    /// </summary>
    public async Task<RecordingFrameWindow> GetWindowAsync(
        RecordingSeekRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RecordingId, nameof(request.RecordingId));
        if (request.StartSequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.StartSequence),
                request.StartSequence,
                "The start sequence cannot be negative.");
        }
        if (request.MaxFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxFrames),
                request.MaxFrames,
                "A playback window must request at least one frame.");
        }

        // Avoid a large speculative allocation for a huge caller-supplied limit on a sparse recording.
        var frames = new List<RecordedFrameBatch>(Math.Min(request.MaxFrames, 1000));
        var hasFollowingFrame = false;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.sequence, f.captured_at, f.pids_json
            FROM recording_frames AS f
            INNER JOIN recordings AS r
                ON r.id = f.recording_id AND r.deleted_at IS NULL
            WHERE f.recording_id = $recordingId
              AND f.sequence >= $startSequence
            ORDER BY f.sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$recordingId", request.RecordingId);
        command.Parameters.AddWithValue("$startSequence", request.StartSequence);
        command.Parameters.AddWithValue("$limit", (long)request.MaxFrames + 1L);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (frames.Count == request.MaxFrames)
            {
                hasFollowingFrame = true;
                break;
            }

            frames.Add(new RecordedFrameBatch
            {
                Sequence = reader.GetInt64(0),
                CapturedAt = reader.GetString(1),
                Pids = RecordingSerialization.DeserializePids(reader.GetString(2)),
            });
        }

        return new RecordingFrameWindow
        {
            StartSequence = request.StartSequence,
            Frames = frames,
            EndOfRecording = !hasFollowingFrame,
        };
    }

    /// <summary>Serializes a runtime frame for storage under the specified parent recording.</summary>
    public static RecordingFrame ToFrame(string recordingId, RecordedFrameBatch frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
        ArgumentNullException.ThrowIfNull(frame);
        return new RecordingFrame
        {
            RecordingId = recordingId,
            Sequence = frame.Sequence,
            CapturedAt = frame.CapturedAt,
            PidsJson = RecordingSerialization.SerializePids(frame.Pids),
        };
    }

    /// <summary>Rehydrates a persisted frame, including each PID value's simulation provenance.</summary>
    public static RecordedFrameBatch ToBatch(RecordingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new RecordedFrameBatch
        {
            Sequence = frame.Sequence,
            CapturedAt = frame.CapturedAt,
            Pids = RecordingSerialization.DeserializePids(frame.PidsJson),
        };
    }

    private async Task<RecordingFrame?> GetOneAsync(
        string keyColumn,
        object key,
        CancellationToken cancellationToken)
    {
        // keyColumn is selected only by the two fixed public methods above, never by user input.
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM recording_frames WHERE {keyColumn} = $key;";
        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new RecordingFrame
        {
            Id = reader.GetInt64(0),
            Guid = reader.GetString(1),
            RecordingId = reader.GetString(2),
            Sequence = reader.GetInt64(3),
            CapturedAt = reader.GetString(4),
            PidsJson = reader.GetString(5),
            CreatedAt = reader.GetString(6),
        };
    }
}
