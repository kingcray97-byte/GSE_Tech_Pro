using System.Data;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;

namespace GseTechPro.Data;

/// <summary>Inserts first-run defaults only; it never creates demo assets or repair history.</summary>
public interface IDbSeeder
{
    /// <summary>Returns true only when the default shop row was inserted.</summary>
    Task<bool> SeedAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SeedData : IDbSeeder
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SeedData(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<bool> SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            long shopCount;
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM shops;";
                shopCount = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Could not read the shop count."));
            }

            if (shopCount != 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var shop = new Shop
            {
                Name = "Default Shop", // Replaced by the customer during the first-run wizard.
                DefaultUnitSystem = UnitSystem.Imperial,
            };

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO shops(
                      id, name, default_unit_system, default_locale, timezone, address, phone,
                      created_at, updated_at)
                    VALUES (
                      $id, $name, $unitSystem, NULL, NULL, NULL, NULL,
                      $createdAt, $updatedAt);
                    """;
                insert.Parameters.AddWithValue("$id", shop.Id);
                insert.Parameters.AddWithValue("$name", shop.Name);
                insert.Parameters.AddWithValue("$unitSystem", DbEnum.ToCode(shop.DefaultUnitSystem));
                insert.Parameters.AddWithValue("$createdAt", shop.CreatedAt);
                insert.Parameters.AddWithValue("$updatedAt", shop.UpdatedAt);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
