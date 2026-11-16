using GseTechPro.Core.Models;

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

public sealed class WorkOrderRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<WorkOrder>(factory, "work_orders");

public sealed class SymptomRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Symptom>(factory, "symptoms");

public sealed class ComplaintRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Complaint>(factory, "complaints");

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

public sealed class ManualRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Manual>(factory, "manuals");
