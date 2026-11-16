namespace GseTechPro.Data.Repositories;

/// <summary>Async CRUD contract for entities with a single TEXT primary key.</summary>
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default);

    Task AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>Updates a live row and returns false when its key does not exist (or is soft-deleted).</summary>
    Task<bool> UpdateAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a live row through deleted_at. Schemas without deleted_at throw
    /// <see cref="NotSupportedException"/> rather than silently destroying data.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds live rows by one mapped entity property or snake_case database column. Raw SQL is not accepted.
    /// </summary>
    Task<IReadOnlyList<T>> FindAsync(
        string column,
        object? value,
        CancellationToken cancellationToken = default);
}
