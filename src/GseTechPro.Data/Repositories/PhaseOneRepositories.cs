using System.Data;
using System.Globalization;
using GseTechPro.Core.Models;
using Microsoft.Data.Sqlite;

namespace GseTechPro.Data.Repositories;

public sealed class ShopRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Shop>(factory, "shops");

public sealed class TechnicianRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Technician>(factory, "technicians");

public sealed class EquipmentModelRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<EquipmentModel>(factory, "equipment_models");

public sealed class AssetRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Asset>(factory, "assets");

public sealed class VehicleRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Vehicle>(factory, "vehicles", "asset_id");

public sealed class WorkOrderRepository : SqliteRepository<WorkOrder>
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public WorkOrderRepository(ISqliteConnectionFactory factory)
        : base(factory, "work_orders")
    {
        _connectionFactory = factory;
    }

    /// <summary>
    /// Returns the next zero-padded six-digit work-order number. Existing null and non-numeric
    /// values are ignored by SQLite's integer cast. The application calls this when creating or
    /// closing a work order, assigns the result to <see cref="WorkOrder.Number"/>, and saves that
    /// work order. Legacy rows are deliberately not backfilled.
    /// </summary>
    /// <remarks>
    /// A short serializable transaction protects the number read on a single-writer tablet.
    /// The unique partial index on <c>work_orders.number</c> remains the final guard at save time.
    /// </remarks>
    public async Task<string> AssignNextNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            long next;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COALESCE(MAX(CAST(number AS INTEGER)), 0) + 1 FROM work_orders;";
                next = Convert.ToInt64(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return next.ToString("D6", CultureInfo.InvariantCulture);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class SymptomRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Symptom>(factory, "symptoms");

public sealed class ComplaintRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Complaint>(factory, "complaints");

/// <summary>
/// Reading provenance follows ADR-009: manually typed, voice-entered, and guided readings set
/// <c>IsSimulated = false</c>. A reading recorded from a recording replay or scan must set
/// <c>IsSimulated = (recordings.source == "simulated")</c>. The Phase 4 application writer
/// supplies that value; this repository persists it in <c>readings.is_simulated</c>.
/// </summary>
public sealed class ReadingRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Reading>(factory, "readings");

public sealed class DtcRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Dtc>(factory, "dtcs");

public sealed class RepairRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Repair>(factory, "repairs");

public sealed class PartRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Part>(factory, "parts");

public sealed class PartReplacedRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<PartReplaced>(factory, "parts_replaced");

public sealed class OutcomeRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Outcome>(factory, "outcomes");

public sealed class NoteRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Note>(factory, "notes");

public sealed class AttachmentRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Attachment>(factory, "attachments");
