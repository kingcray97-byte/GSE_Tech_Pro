using System.Data;
using System.Globalization;
using System.Text.Json;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Services;

/// <summary>A possible prior work order returned by comeback detection; nothing is persisted yet.</summary>
public sealed record ComebackMatch(
    string OriginalWorkOrderId,
    string ComebackWorkOrderId,
    string MatchReason,
    int WindowDays,
    double WindowHours);

/// <summary>
/// Detects possible comebacks for the application (ADR-010 §6). Detection is read-only: the app
/// decides whether to persist a prompt with <see cref="RecordDetectedAsync"/>, and only a technician
/// confirmation changes the learning signal through <see cref="MarkConfirmedAsync"/> or
/// <see cref="ConfirmAsync"/>.
/// </summary>
public sealed class ComebackDetectionService
{
    public const int DefaultWindowDays = 14;
    public const double DefaultWindowHours = 200d;

    private const string WindowDaysSettingKey = "comeback.window_days";
    private const string WindowHoursSettingKey = "comeback.window_hours";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public ComebackDetectionService(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Finds prior closed work orders on the closing work order's asset, within the configured
    /// day/hour bounds, that share a repair system, symptom, or case-insensitive DTC code/protocol.
    /// When both time and hour-meter evidence are available, both bounds must hold. When only one
    /// is available, that bound alone is used. If neither is available, no candidate is returned.
    /// The returned candidates are not inserted into <c>comebacks</c>.
    /// </summary>
    public async Task<IReadOnlyList<ComebackMatch>> DetectAsync(
        string closingWorkOrderId,
        CancellationToken cancellationToken = default)
    {
        RequireId(closingWorkOrderId, nameof(closingWorkOrderId));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var closing = await ReadClosingContextAsync(connection, closingWorkOrderId, cancellationToken)
            .ConfigureAwait(false);
        if (closing is null)
        {
            throw new InvalidOperationException(
                $"Cannot detect comebacks because work order '{closingWorkOrderId}' was not found or is deleted.");
        }

        var (windowDays, windowHours) = await ReadWindowAsync(connection, cancellationToken).ConfigureAwait(false);
        var priorWorkOrders = await ReadPriorClosedWorkOrdersAsync(
            connection, closing.AssetId, closingWorkOrderId, cancellationToken).ConfigureAwait(false);

        DateTimeOffset? closingClosedAt = ParseTimestamp(closing.ClosedAt);
        var matches = new List<ComebackMatch>();
        foreach (var prior in priorWorkOrders)
        {
            if (!IsWithinWindow(closingClosedAt, closing.CurrentHours, prior, windowDays, windowHours))
                continue;

            var reason = await FindSharedSignalReasonAsync(
                connection, closingWorkOrderId, prior.Id, cancellationToken).ConfigureAwait(false);
            if (reason is null)
                continue;

            matches.Add(new ComebackMatch(
                prior.Id,
                closingWorkOrderId,
                reason,
                windowDays,
                windowHours));
        }

        return matches;
    }

    /// <summary>
    /// Persists an auto-detected, unconfirmed row (<c>auto=1, confirmed=0</c>) so the application
    /// can retain the technician prompt state. Repeating the same work-order pair returns its existing
    /// row instead of inserting a duplicate. Unconfirmed rows do not demote repair success rates.
    /// Call <see cref="MarkConfirmedAsync"/> only after the technician confirms the comeback.
    /// </summary>
    public async Task<Comeback> RecordDetectedAsync(
        ComebackMatch match,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(match);
        RequireId(match.OriginalWorkOrderId, nameof(match.OriginalWorkOrderId));
        RequireId(match.ComebackWorkOrderId, nameof(match.ComebackWorkOrderId));

        var now = Clock.UtcNow;
        var comeback = CreateComeback(match, confirmed: false, now);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetOrInsertComebackAsync(connection, transaction: null, comeback, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a previously recorded candidate confirmed and, in the same transaction, sets
    /// <c>outcomes.comeback_detected=1</c> on its original work order when that outcome exists.
    /// Returns false when the comeback id does not exist. Repeating a confirmation is idempotent.
    /// </summary>
    public async Task<bool> MarkConfirmedAsync(
        string comebackId,
        CancellationToken cancellationToken = default)
    {
        RequireId(comebackId, nameof(comebackId));

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            string? originalWorkOrderId;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT original_work_order_id FROM comebacks WHERE id = $id;";
                find.Parameters.AddWithValue("$id", comebackId);
                originalWorkOrderId = Convert.ToString(
                    await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            if (originalWorkOrderId is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await using (var confirm = connection.CreateCommand())
            {
                confirm.Transaction = transaction;
                confirm.CommandText = "UPDATE comebacks SET confirmed = 1 WHERE id = $id;";
                confirm.Parameters.AddWithValue("$id", comebackId);
                await confirm.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await MirrorOutcomeAsync(
                connection, transaction, originalWorkOrderId, Clock.UtcNow, cancellationToken).ConfigureAwait(false);
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
    /// Inserts an auto-detected row that has already been confirmed by the technician
    /// (<c>auto=1, confirmed=1</c>) and mirrors confirmation to the original work order's outcome
    /// in one transaction. An existing row for the work-order pair is confirmed and returned instead
    /// of duplicated. Prefer <see cref="RecordDetectedAsync"/> followed by <see cref="MarkConfirmedAsync"/>
    /// when the unconfirmed prompt must first be persisted.
    /// </summary>
    public async Task<Comeback> ConfirmAsync(
        string originalWorkOrderId,
        string comebackWorkOrderId,
        int windowDays,
        double windowHours,
        string? matchReason,
        CancellationToken cancellationToken = default)
    {
        RequireId(originalWorkOrderId, nameof(originalWorkOrderId));
        RequireId(comebackWorkOrderId, nameof(comebackWorkOrderId));

        var match = new ComebackMatch(
            originalWorkOrderId,
            comebackWorkOrderId,
            matchReason ?? string.Empty,
            windowDays,
            windowHours);
        var now = Clock.UtcNow;
        var comeback = CreateComeback(match, confirmed: true, now);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var persisted = await GetOrInsertComebackAsync(
                connection, transaction, comeback, cancellationToken).ConfigureAwait(false);
            if (!persisted.Confirmed)
            {
                await using var confirm = connection.CreateCommand();
                confirm.Transaction = transaction;
                confirm.CommandText = "UPDATE comebacks SET confirmed = 1 WHERE id = $id;";
                confirm.Parameters.AddWithValue("$id", persisted.Id);
                await confirm.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                persisted = persisted with { Confirmed = true };
            }

            await MirrorOutcomeAsync(
                connection, transaction, originalWorkOrderId, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return persisted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ClosingContext?> ReadClosingContextAsync(
        SqliteConnection connection,
        string closingWorkOrderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.asset_id, w.closed_at, w.hours_at_open, a.hour_meter
            FROM work_orders w
            JOIN assets a ON a.id = w.asset_id
            WHERE w.id = $id AND w.deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", closingWorkOrderId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var hoursAtOpen = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? (double?)null
            : reader.GetDouble(2);
        var assetHourMeter = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
            ? (double?)null
            : reader.GetDouble(3);

        // The closing WO's timestamped opening meter reflects when the repeat failure returned.
        // Fall back to the asset's live meter only when no opening-meter reading was captured.
        var currentHours = hoursAtOpen ?? assetHourMeter;
        return new ClosingContext(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), currentHours);
    }

    private static async Task<IReadOnlyList<PriorContext>> ReadPriorClosedWorkOrdersAsync(
        SqliteConnection connection,
        string assetId,
        string closingWorkOrderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, closed_at, hours_at_open
            FROM work_orders
            WHERE asset_id = $assetId
              AND id <> $closingId
              AND status = 'closed'
              AND closed_at IS NOT NULL
              AND deleted_at IS NULL
            ORDER BY closed_at DESC, id;
            """;
        command.Parameters.AddWithValue("$assetId", assetId);
        command.Parameters.AddWithValue("$closingId", closingWorkOrderId);

        var rows = new List<PriorContext>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hoursAtOpen = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? (double?)null
                : reader.GetDouble(2);
            rows.Add(new PriorContext(reader.GetString(0), reader.GetString(1), hoursAtOpen));
        }

        return rows;
    }

    private static bool IsWithinWindow(
        DateTimeOffset? closingClosedAt,
        double? currentHours,
        PriorContext prior,
        int windowDays,
        double windowHours)
    {
        var priorClosedAt = ParseTimestamp(prior.ClosedAt);
        var daysKnown = closingClosedAt.HasValue && priorClosedAt.HasValue;
        if (daysKnown)
        {
            var elapsed = closingClosedAt!.Value - priorClosedAt!.Value;
            if (elapsed < TimeSpan.Zero || elapsed.TotalDays > windowDays)
                return false;
        }

        var hoursKnown = currentHours.HasValue && prior.HoursAtOpen.HasValue &&
            double.IsFinite(currentHours.Value) && double.IsFinite(prior.HoursAtOpen.Value);
        if (hoursKnown)
        {
            var elapsedHours = currentHours!.Value - prior.HoursAtOpen!.Value;
            if (elapsedHours < 0 || elapsedHours > windowHours)
                return false;
        }

        // Days alone, hours alone, or both (both were checked above) are valid. Neither is not.
        return daysKnown || hoursKnown;
    }

    private static async Task<string?> FindSharedSignalReasonAsync(
        SqliteConnection connection,
        string closingWorkOrderId,
        string originalWorkOrderId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT sort_order, signal_key, description
            FROM (
              SELECT 1 AS sort_order,
                     'system:' || lower(trim(closing_repair.system)) AS signal_key,
                     'system ''' || trim(closing_repair.system) || '''' AS description
              FROM repairs closing_repair
              JOIN repairs original_repair
                ON original_repair.work_order_id = $originalId
               AND lower(trim(original_repair.system)) = lower(trim(closing_repair.system))
              WHERE closing_repair.work_order_id = $closingId
                AND closing_repair.deleted_at IS NULL
                AND original_repair.deleted_at IS NULL
                AND closing_repair.system IS NOT NULL
                AND trim(closing_repair.system) <> ''
                AND original_repair.system IS NOT NULL
                AND trim(original_repair.system) <> ''

              UNION ALL

              SELECT 2 AS sort_order,
                     'symptom:' || closing_symptom.symptom_id AS signal_key,
                     'symptom ''' || closing_symptom.symptom_id || ''' (' ||
                       COALESCE(NULLIF(trim(symptom.name), ''), closing_symptom.symptom_id) || ')' AS description
              FROM work_order_symptoms closing_symptom
              JOIN work_order_symptoms original_symptom
                ON original_symptom.work_order_id = $originalId
               AND original_symptom.symptom_id = closing_symptom.symptom_id
              LEFT JOIN symptoms symptom ON symptom.id = closing_symptom.symptom_id
              WHERE closing_symptom.work_order_id = $closingId

              UNION ALL

              SELECT 3 AS sort_order,
                     'dtc:' || lower(trim(closing_dtc.code)) || '|' || lower(trim(closing_dtc.protocol)) AS signal_key,
                     'DTC ''' || closing_dtc.protocol || ' ' || lower(trim(closing_dtc.code)) || '''' AS description
              FROM dtcs closing_dtc
              JOIN dtcs original_dtc
                ON original_dtc.work_order_id = $originalId
               AND lower(trim(original_dtc.code)) = lower(trim(closing_dtc.code))
               AND lower(trim(original_dtc.protocol)) = lower(trim(closing_dtc.protocol))
              WHERE closing_dtc.work_order_id = $closingId
                AND closing_dtc.deleted_at IS NULL
                AND original_dtc.deleted_at IS NULL
            )
            ORDER BY sort_order, signal_key, description;
            """;
        command.Parameters.AddWithValue("$closingId", closingWorkOrderId);
        command.Parameters.AddWithValue("$originalId", originalWorkOrderId);

        var descriptions = new List<string>();
        var seen = new HashSet<(int SortOrder, string SignalKey)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (reader.GetInt32(0), reader.GetString(1));
            if (seen.Add(key))
                descriptions.Add(reader.GetString(2));
        }

        return descriptions.Count == 0 ? null : "Shared " + string.Join("; ", descriptions) + ".";
    }

    private static async Task<(int Days, double Hours)> ReadWindowAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var days = DefaultWindowDays;
        var hours = DefaultWindowHours;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_json FROM app_settings WHERE key IN ($daysKey, $hoursKey);";
        command.Parameters.AddWithValue("$daysKey", WindowDaysSettingKey);
        command.Parameters.AddWithValue("$hoursKey", WindowHoursSettingKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            var rawValue = reader.GetString(1);
            if (string.Equals(key, WindowDaysSettingKey, StringComparison.Ordinal) &&
                TryReadPositiveInt32(rawValue, out var configuredDays))
            {
                days = configuredDays;
            }
            else if (string.Equals(key, WindowHoursSettingKey, StringComparison.Ordinal) &&
                TryReadPositiveDouble(rawValue, out var configuredHours))
            {
                hours = configuredHours;
            }
        }

        return (days, hours);
    }

    private static bool TryReadPositiveInt32(string json, out int value)
    {
        value = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var element = document.RootElement;
            var parsed = element.ValueKind == JsonValueKind.Number
                ? element.TryGetInt32(out value)
                : element.ValueKind == JsonValueKind.String &&
                  int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            return parsed && value > 0;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static bool TryReadPositiveDouble(string json, out double value)
    {
        value = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var element = document.RootElement;
            var parsed = element.ValueKind == JsonValueKind.Number
                ? element.TryGetDouble(out value)
                : element.ValueKind == JsonValueKind.String &&
                  double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return parsed && double.IsFinite(value) && value > 0;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp)
            ? timestamp
            : null;

    private static Comeback CreateComeback(ComebackMatch match, bool confirmed, string now) => new()
    {
        OriginalWorkOrderId = match.OriginalWorkOrderId,
        ComebackWorkOrderId = match.ComebackWorkOrderId,
        MatchReason = match.MatchReason,
        WindowDays = match.WindowDays,
        WindowHours = match.WindowHours,
        Auto = true,
        Confirmed = confirmed,
        DetectedAt = now,
        CreatedAt = now,
    };

    private static async Task<Comeback> GetOrInsertComebackAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Comeback comeback,
        CancellationToken cancellationToken)
    {
        // Closing the same work order again must not accumulate links for the same WO pair. Include
        // the id in the ordering so the returned row remains deterministic for any legacy duplicates.
        await using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT id, original_work_order_id, comeback_work_order_id, match_reason,
                       window_days, window_hours, auto, confirmed, detected_at, created_at
                FROM comebacks
                WHERE original_work_order_id = $originalId
                  AND comeback_work_order_id = $comebackId
                ORDER BY detected_at, id
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$originalId", comeback.OriginalWorkOrderId);
            find.Parameters.AddWithValue("$comebackId", comeback.ComebackWorkOrderId);

            await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new Comeback
                {
                    Id = reader.GetString(0),
                    OriginalWorkOrderId = reader.GetString(1),
                    ComebackWorkOrderId = reader.GetString(2),
                    MatchReason = reader.IsDBNull(3) ? null : reader.GetString(3),
                    WindowDays = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    WindowHours = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    Auto = reader.GetInt32(6) != 0,
                    Confirmed = reader.GetInt32(7) != 0,
                    DetectedAt = reader.GetString(8),
                    CreatedAt = reader.GetString(9),
                };
            }
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO comebacks (
              id, original_work_order_id, comeback_work_order_id, match_reason,
              window_days, window_hours, auto, confirmed, detected_at, created_at)
            VALUES (
              $id, $originalId, $comebackId, $matchReason,
              $windowDays, $windowHours, 1, $confirmed, $detectedAt, $createdAt);
            """;
        insert.Parameters.AddWithValue("$id", comeback.Id);
        insert.Parameters.AddWithValue("$originalId", comeback.OriginalWorkOrderId);
        insert.Parameters.AddWithValue("$comebackId", comeback.ComebackWorkOrderId);
        insert.Parameters.AddWithValue("$matchReason", (object?)comeback.MatchReason ?? DBNull.Value);
        insert.Parameters.AddWithValue("$windowDays", (object?)comeback.WindowDays ?? DBNull.Value);
        insert.Parameters.AddWithValue("$windowHours", (object?)comeback.WindowHours ?? DBNull.Value);
        insert.Parameters.AddWithValue("$confirmed", comeback.Confirmed ? 1 : 0);
        insert.Parameters.AddWithValue("$detectedAt", comeback.DetectedAt);
        insert.Parameters.AddWithValue("$createdAt", comeback.CreatedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return comeback;
    }

    private static async Task MirrorOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string originalWorkOrderId,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE outcomes
            SET comeback_detected = 1, updated_at = $updatedAt
            WHERE work_order_id = $originalId;
            """;
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$originalId", originalWorkOrderId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RequireId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A work-order or comeback id is required.", parameterName);
    }

    private sealed record ClosingContext(string AssetId, string? ClosedAt, double? CurrentHours);

    private sealed record PriorContext(string Id, string ClosedAt, double? HoursAtOpen);
}
