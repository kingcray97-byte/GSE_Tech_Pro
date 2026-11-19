using GseTechPro.Core.Models;

namespace GseTechPro.Data.Repositories;

public sealed class SafetyWarningRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<SafetyWarning>(factory, "safety_warnings");

public sealed class TestPointRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<TestPoint>(factory, "test_points");

public sealed class TroubleshootingTreeRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<TroubleshootingTree>(factory, "troubleshooting_trees");

public sealed class TroubleshootingStepRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<TroubleshootingStep>(factory, "troubleshooting_steps");
