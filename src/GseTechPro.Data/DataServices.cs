using GseTechPro.Data.Repositories;

namespace GseTechPro.Data;

/// <summary>
/// Plain factory/composition root for the App and tests. No external DI container is required.
/// Create one instance per application database and call <see cref="InitializeAsync"/> at startup.
/// </summary>
public sealed class DataServices
{
    public DataServices(DatabaseOptions? options = null)
    {
        Options = options ?? new DatabaseOptions();
        Connections = new SqliteConnectionFactory(Options);
        Migrator = new Migrations.SqliteMigrator(Connections);
        Seeder = new SeedData(Connections);
        Initializer = new DatabaseInitializer(Connections, Options, Seeder);
        Backup = new DatabaseBackup(Connections);

        Shops = new ShopRepository(Connections);
        Technicians = new TechnicianRepository(Connections);
        EquipmentModels = new EquipmentModelRepository(Connections);
        Assets = new AssetRepository(Connections);
        Vehicles = new VehicleRepository(Connections);
        WorkOrders = new WorkOrderRepository(Connections);
        Symptoms = new SymptomRepository(Connections);
        Complaints = new ComplaintRepository(Connections);
        Readings = new ReadingRepository(Connections);
        Dtcs = new DtcRepository(Connections);
        Repairs = new RepairRepository(Connections);
        Parts = new PartRepository(Connections);
        PartsReplaced = new PartReplacedRepository(Connections);
        Outcomes = new OutcomeRepository(Connections);
        Notes = new NoteRepository(Connections);
        Attachments = new AttachmentRepository(Connections);
        Manuals = new ManualRepository(Connections);
    }

    public DatabaseOptions Options { get; }

    public ISqliteConnectionFactory Connections { get; }

    public Migrations.SqliteMigrator Migrator { get; }

    public IDbSeeder Seeder { get; }

    public DatabaseInitializer Initializer { get; }

    public DatabaseBackup Backup { get; }

    public ShopRepository Shops { get; }

    public TechnicianRepository Technicians { get; }

    public EquipmentModelRepository EquipmentModels { get; }

    public AssetRepository Assets { get; }

    public VehicleRepository Vehicles { get; }

    public WorkOrderRepository WorkOrders { get; }

    public SymptomRepository Symptoms { get; }

    public ComplaintRepository Complaints { get; }

    public ReadingRepository Readings { get; }

    public DtcRepository Dtcs { get; }

    public RepairRepository Repairs { get; }

    public PartRepository Parts { get; }

    public PartReplacedRepository PartsReplaced { get; }

    public OutcomeRepository Outcomes { get; }

    public NoteRepository Notes { get; }

    public AttachmentRepository Attachments { get; }

    public ManualRepository Manuals { get; }

    public Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        Initializer.InitializeAsync(cancellationToken);
}
