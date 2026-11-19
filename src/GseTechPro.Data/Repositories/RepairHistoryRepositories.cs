using GseTechPro.Core.Models;

namespace GseTechPro.Data.Repositories;

/// <summary>Writes diagnostic conclusions used as repair-history evidence.</summary>
public sealed class DiagnosisRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Diagnosis>(factory, "diagnoses");

/// <summary>Writes possible and technician-confirmed comeback links.</summary>
public sealed class ComebackRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Comeback>(factory, "comebacks");

/// <summary>Writes the memoized recommendation audit trail for the application.</summary>
public sealed class RecommendationRepository(ISqliteConnectionFactory factory)
    : SqliteRepository<Recommendation>(factory, "recommendations");
