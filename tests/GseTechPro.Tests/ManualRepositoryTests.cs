using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Data;
using GseTechPro.Data.Migrations;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GseTechPro.Tests;

public sealed class ManualRepositoryTests : IAsyncLifetime
{
    private string _dbPath = "";
    private DataServices _services = null!;
    private DatabaseInitializationResult _initialization = null!;

    public async Task InitializeAsync()
    {
        _dbPath = NewDatabasePath("manuals");
        _services = new DataServices(new DatabaseOptions { DatabasePath = _dbPath });
        _initialization = await _services.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        DeleteDatabase(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FreshDatabase_WithBundledVec_CreatesVersionSixAndEmbeddings()
    {
        Assert.Equal(7, SqliteMigrator.LatestVersion);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, _initialization.AppliedMigrations);
        Assert.True(_initialization.VectorSearchAvailable, string.Join(Environment.NewLine, _initialization.Notices));
        Assert.True(await _services.IsVec0AvailableAsync());

        await using var connection = await _services.Connections.OpenAsync();
        Assert.True(await SqliteVecExtension.IsVec0AvailableAsync(connection));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'embeddings';";
        var ddl = Convert.ToString(await command.ExecuteScalarAsync());
        Assert.NotNull(ddl);
        Assert.Contains("vec0", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("float[768]", ddl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingVecExtension_FallsBack_AndRecoversOnSecondStartup()
    {
        var dbPath = NewDatabasePath("manuals-no-vec");
        try
        {
            {
                var options = new DatabaseOptions
                {
                    DatabasePath = dbPath,
                    SqliteVecExtensionPath = dbPath + ".missing-vec0.dll",
                };
                var services = new DataServices(options);
                var result = await services.InitializeAsync();

                Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, result.AppliedMigrations);
                Assert.False(result.VectorSearchAvailable);
                Assert.Contains(result.Notices, notice => notice.Contains("vec", StringComparison.OrdinalIgnoreCase));
                Assert.False(await services.IsVec0AvailableAsync());

                // Reopening through the same factory exercises the cached negative load result.
                for (var open = 0; open < 3; open++)
                {
                    await using var connection = await services.Connections.OpenAsync();
                    Assert.False(await SqliteVecExtension.IsVec0AvailableAsync(connection));

                    if (open == 0)
                    {
                        await using var version = connection.CreateCommand();
                        version.CommandText = "SELECT MAX(version) FROM schema_migrations;";
                        Assert.Equal(7L, await version.ExecuteScalarAsync());

                        await using var table = connection.CreateCommand();
                        table.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'embeddings';";
                        Assert.Null(await table.ExecuteScalarAsync());
                    }
                }

                // Embedding APIs are deliberate no-ops on the FTS-only fallback path.
                Assert.False(await services.Embeddings.StoreAsync(1, new float[SqliteVecExtension.EmbeddingDimensions]));
                Assert.Empty(await services.Embeddings.FindNearestChunkIdsAsync(
                    new float[SqliteVecExtension.EmbeddingDimensions]));
            }

            var bundledPath = Path.Combine(
                AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vec0.dll");
            Assert.True(File.Exists(bundledPath), $"Bundled sqlite-vec was not found at {bundledPath}");

            var recoveredServices = new DataServices(new DatabaseOptions
            {
                DatabasePath = dbPath,
                SqliteVecExtensionPath = bundledPath,
            });
            var recovered = await recoveredServices.InitializeAsync();

            Assert.Empty(recovered.AppliedMigrations);
            Assert.True(recovered.VectorSearchAvailable, string.Join(Environment.NewLine, recovered.Notices));
            Assert.True(await recoveredServices.IsVec0AvailableAsync());
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Upsert_DedupesLiveManualByFileHash()
    {
        var fileHash = "sha256-" + Guid.NewGuid().ToString("N");
        var original = NewManual(fileHash);
        var first = await _services.Manuals.UpsertAsync(original);
        var duplicate = await _services.Manuals.UpsertAsync(NewManual(fileHash));

        Assert.True(first.Inserted);
        Assert.False(duplicate.Inserted);
        Assert.Equal(first.Manual.Id, duplicate.Manual.Id);
        Assert.Equal(first.Manual.Id, original.Id);
        Assert.Single(await _services.Manuals.FindAsync("file_hash", fileHash));

        var fetched = await _services.Manuals.GetByFileHashAsync(fileHash);
        Assert.NotNull(fetched);
        Assert.Equal(original.Id, fetched.Id);
        Assert.Equal(ManualFormat.Pdf, fetched.Format);
        Assert.Equal(ManualStatus.Imported, fetched.Status);
    }

    [Fact]
    public async Task PagesChunksAndTags_RoundTrip_WithRowidsGuidsAndPageRanges()
    {
        var manual = (await _services.Manuals.UpsertAsync(NewManual())).Manual;
        var pages = await _services.ManualPages.InsertAsync(
        [
            NewPage(manual.Id, 2),
            NewPage(manual.Id, 3),
        ]);

        Assert.All(pages, page => Assert.True(page.Id > 0));
        Assert.Equal(pages[0].Id, (await _services.ManualPages.GetByIdAsync(pages[0].Id))!.Id);
        Assert.Equal(pages[1].Guid, (await _services.ManualPages.GetByGuidAsync(pages[1].Guid))!.Guid);

        var chunks = await _services.ManualChunks.InsertAsync(
        [
            NewChunk(manual.Id, 2, 3, "pressure test text"),
            NewChunk(manual.Id, 7, 7, "electrical wiring text"),
        ]);

        Assert.All(chunks, chunk => Assert.True(chunk.Id > 0));
        Assert.Equal(chunks[0].ChunkText, (await _services.ManualChunks.GetByIdAsync(chunks[0].Id))!.ChunkText);
        Assert.Equal(chunks[1].Guid, (await _services.ManualChunks.GetByGuidAsync(chunks[1].Guid))!.Guid);
        Assert.Equal(ChunkKind.Text, (await _services.ManualChunks.GetByIdAsync(chunks[0].Id))!.ChunkKind);

        var overlap = await _services.ManualChunks.ListByManualAsync(manual.Id, 3, 7);
        Assert.Equal(chunks.Select(chunk => chunk.Id), overlap.Select(chunk => chunk.Id));
        var one = await _services.ManualChunks.ListByManualAsync(manual.Id, 2, 2);
        Assert.Equal(chunks[0].Id, Assert.Single(one).Id);
        Assert.Empty(await _services.ManualChunks.ListByManualAsync(manual.Id, 4, 6));

        Assert.True(await _services.ChunkTags.AddAsync(new ChunkTag { ChunkId = chunks[0].Id, Tag = "hydraulics" }));
        Assert.False(await _services.ChunkTags.AddAsync(new ChunkTag { ChunkId = chunks[0].Id, Tag = "hydraulics" }));
        Assert.Equal(2, await _services.ChunkTags.AddRangeAsync(chunks[0].Id, ["test-port", "hydraulics", "pump"]));
        Assert.Equal(2, (await _services.ManualPages.ListByManualAsync(manual.Id)).Count);
        Assert.Equal(
            new[] { "hydraulics", "pump", "test-port" },
            await _services.ChunkTags.ListTagsAsync(chunks[0].Id));
    }

    [Fact]
    public async Task FtsSearch_UsesBm25_AndExcludesSoftDeletedChunksAndManuals()
    {
        var manual = (await _services.Manuals.UpsertAsync(NewManual())).Manual;
        var searchToken = "accumulatorzz";
        var chunks = await _services.ManualChunks.InsertAsync(
        [
            NewChunk(manual.Id, 1, 1, $"{searchToken} first matching chunk"),
            NewChunk(manual.Id, 2, 2, $"{searchToken} second matching chunk"),
        ]);
        await _services.ManualPages.InsertAsync(NewPage(manual.Id, 1));

        var beforeDelete = await _services.ManualChunks.SearchFtsAsync(searchToken);
        Assert.Equal(2, beforeDelete.Count);
        Assert.All(beforeDelete, hit => Assert.True(double.IsFinite(hit.Score)));

        Assert.True(await _services.ManualChunks.DeleteAsync(chunks[0].Id));
        var afterChunkDelete = await _services.ManualChunks.SearchFtsAsync(searchToken);
        Assert.Equal(chunks[1].Id, Assert.Single(afterChunkDelete).Chunk.Id);
        Assert.Null(await _services.ManualChunks.GetByIdAsync(chunks[0].Id));
        Assert.NotNull(await _services.ManualChunks.GetByGuidAsync(chunks[1].Guid));

        Assert.True(await _services.Manuals.DeleteAsync(manual.Id));
        Assert.Empty(await _services.ManualChunks.SearchFtsAsync(searchToken));
        Assert.Empty(await _services.ManualChunks.ListByManualAsync(manual.Id));
        Assert.Empty(await _services.ManualPages.ListByManualAsync(manual.Id));
        Assert.Null(await _services.Manuals.GetByIdAsync(manual.Id));
        Assert.False(await _services.Manuals.DeleteAsync(manual.Id));
    }

    [Fact]
    public async Task FtsSearch_QuotesHyphensAndPunctuationWithoutParserErrors()
    {
        var manual = (await _services.Manuals.UpsertAsync(NewManual())).Manual;
        await _services.ManualChunks.InsertAsync(
            NewChunk(manual.Id, 1, 1, "TUG 660-28 hydraulic pump service procedure"));

        Assert.Single(await _services.ManualChunks.SearchFtsAsync("660-28"));
        Assert.Single(await _services.ManualChunks.SearchFtsAsync("TUG 660-28"));
        Assert.Empty(await _services.ManualChunks.SearchFtsAsync("?"));
        Assert.Empty(await _services.ManualChunks.SearchFtsAsync("\""));
    }
    [Fact]
    public async Task Embeddings_StoreReplaceAndKnn_ReturnClosestLiveChunkIds()
    {
        Assert.True(_initialization.VectorSearchAvailable, string.Join(Environment.NewLine, _initialization.Notices));
        var manual = (await _services.Manuals.UpsertAsync(NewManual())).Manual;
        var chunks = await _services.ManualChunks.InsertAsync(
        [
            NewChunk(manual.Id, 1, 1, "closest vector"),
            NewChunk(manual.Id, 2, 2, "second vector"),
            NewChunk(manual.Id, 3, 3, "far vector"),
        ]);

        var closest = Vector(0, 1.0f);
        var second = Vector(0, 0.9f);
        var far = Vector(1, 1.0f);
        var query = Vector(0, 1.0f);
        Assert.True(await _services.Embeddings.StoreAsync(chunks[0].Id, closest));
        Assert.True(await _services.Embeddings.StoreAsync(chunks[1].Id, second));
        Assert.True(await _services.Embeddings.StoreAsync(chunks[2].Id, far));

        var nearest = await _services.Embeddings.FindNearestChunkIdsAsync(query, 3);
        Assert.Equal(chunks.Select(chunk => chunk.Id), nearest);

        // Store is replace-by-chunk-id. Deleting the closest vector must remove its vec0 row;
        // otherwise k=1 would consume the only slot on the now-soft-deleted closest chunk.
        Assert.True(await _services.Embeddings.StoreAsync(chunks[0].Id, closest));
        Assert.True(await _services.ManualChunks.DeleteAsync(chunks[0].Id));
        var afterChunkDelete = await _services.Embeddings.FindNearestChunkIdsAsync(query, 1);
        Assert.Equal(chunks[1].Id, Assert.Single(afterChunkDelete));

        await using (var connection = await _services.Connections.OpenAsync())
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM embeddings;";
            Assert.Equal(2L, await count.ExecuteScalarAsync());
        }

        Assert.True(await _services.Manuals.DeleteAsync(manual.Id));
        Assert.Empty(await _services.Embeddings.FindNearestChunkIdsAsync(query, 3));
        await using (var connection = await _services.Connections.OpenAsync())
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM embeddings;";
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
    }

    private Manual NewManual(string? fileHash = null) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Title = "Repository Test Manual",
        Manufacturer = "Test Manufacturer",
        ModelFamily = "Test Family",
        FilePath = "manuals/test.pdf",
        FileHash = fileHash ?? "sha256-" + Guid.NewGuid().ToString("N"),
        PageCount = 10,
        Format = ManualFormat.Pdf,
        Status = ManualStatus.Imported,
    };

    private static ManualPage NewPage(string manualId, int pageNumber) => new()
    {
        Guid = Guid.NewGuid().ToString("D"),
        ManualId = manualId,
        PageNumber = pageNumber,
        Section = "Testing",
        HasImage = pageNumber % 2 == 0,
        ThumbnailPath = null,
    };

    private static ManualChunk NewChunk(string manualId, int start, int end, string text) => new()
    {
        Guid = Guid.NewGuid().ToString("D"),
        ManualId = manualId,
        PageStart = start,
        PageEnd = end,
        Section = "Testing",
        ChunkKind = ChunkKind.Text,
        Heading = "Repository test",
        ChunkText = text,
    };

    private static float[] Vector(int index, float value)
    {
        var vector = new float[SqliteVecExtension.EmbeddingDimensions];
        vector[index] = value;
        return vector;
    }

    private static string NewDatabasePath(string prefix)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gse-tech-pro-tests");
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"{prefix}-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string dbPath)
    {
        try
        {
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
        }
        catch (IOException)
        {
            // Best-effort test cleanup; Windows may hold a pooled native connection briefly.
        }
    }
}
