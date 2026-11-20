using GseTechPro.Data.Repositories;
using GseTechPro.Data.Services;

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
        DtcLibrarySeeder = new DtcLibrarySeeder(Connections);
        TroubleshootingSeeder = new TroubleshootingSeeder(Connections);
        ScopeSeeder = new ScopeSeeder(Connections);
        Initializer = new DatabaseInitializer(
            Connections, Options, Seeder, DtcLibrarySeeder, TroubleshootingSeeder, ScopeSeeder);
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
        Diagnoses = new DiagnosisRepository(Connections);
        Comebacks = new ComebackRepository(Connections);
        Recommendations = new RecommendationRepository(Connections);
        ComebackDetection = new ComebackDetectionService(Connections);
        Notes = new NoteRepository(Connections);
        Attachments = new AttachmentRepository(Connections);
        Manuals = new ManualRepository(Connections);
        ManualPages = new ManualPageRepository(Connections);
        ManualChunks = new ManualChunkRepository(Connections);
        ChunkTags = new ChunkTagRepository(Connections);
        Embeddings = new EmbeddingRepository(Connections);
        Recordings = new RecordingRepository(Connections);
        RecordingFrames = new RecordingFrameRepository(Connections);
        SafetyWarnings = new SafetyWarningRepository(Connections);
        TestPoints = new TestPointRepository(Connections);
        TroubleshootingTrees = new TroubleshootingTreeRepository(Connections);
        TroubleshootingSteps = new TroubleshootingStepRepository(Connections);
        GuidedTests = new GuidedTestRepository(Connections);
        ReferenceWaveforms = new ReferenceWaveformRepository(Connections);
        Waveforms = new WaveformRepository(Connections);
        Settings = new AppSettingsRepository(Connections);
    }

    public DatabaseOptions Options { get; }

    public ISqliteConnectionFactory Connections { get; }

    public Migrations.SqliteMigrator Migrator { get; }

    public IDbSeeder Seeder { get; }

    public IDtcLibrarySeeder DtcLibrarySeeder { get; }

    public ITroubleshootingSeeder TroubleshootingSeeder { get; }

    public IScopeSeeder ScopeSeeder { get; }

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

    public DiagnosisRepository Diagnoses { get; }

    public ComebackRepository Comebacks { get; }

    public RecommendationRepository Recommendations { get; }

    /// <summary>Read-only detection plus explicit application writes for comeback confirmation.</summary>
    public ComebackDetectionService ComebackDetection { get; }

    public NoteRepository Notes { get; }

    public AttachmentRepository Attachments { get; }

    public ManualRepository Manuals { get; }

    public ManualPageRepository ManualPages { get; }

    public ManualChunkRepository ManualChunks { get; }

    public ChunkTagRepository ChunkTags { get; }

    public EmbeddingRepository Embeddings { get; }

    public RecordingRepository Recordings { get; }

    public RecordingFrameRepository RecordingFrames { get; }

    public SafetyWarningRepository SafetyWarnings { get; }

    public TestPointRepository TestPoints { get; }

    public TroubleshootingTreeRepository TroubleshootingTrees { get; }

    public TroubleshootingStepRepository TroubleshootingSteps { get; }

    public GuidedTestRepository GuidedTests { get; }

    public ReferenceWaveformRepository ReferenceWaveforms { get; }

    public WaveformRepository Waveforms { get; }

    public AppSettingsRepository Settings { get; }

    public Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        Initializer.InitializeAsync(cancellationToken);

    /// <summary>True only when both vec0 and the embeddings table are ready.</summary>
    public Task<bool> IsVec0AvailableAsync(CancellationToken cancellationToken = default) =>
        Embeddings.IsVec0AvailableAsync(cancellationToken);
}
