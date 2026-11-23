using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Models;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>Manual Search › Import manuals: POST /ingest then GET /ingest/status until the job ends, through the real HTTP client.</summary>
public sealed class ManualImportTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Answers /ingest with a job id and /ingest/status with the scripted bodies in order (the last one repeats).</summary>
    private sealed class IngestHandler(HttpStatusCode ingestStatus, string ingestBody, params string[] statusBodies) : HttpMessageHandler
    {
        private int _statusCalls;
        public JsonObject? IngestBody { get; private set; }
        public int IngestCalls { get; private set; }
        public List<string> StatusQueries { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path == "/ingest")
            {
                IngestCalls++;
                IngestBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))?.AsObject();
                return Json(ingestStatus, ingestBody);
            }
            if (path == "/ingest/status")
            {
                StatusQueries.Add(request.RequestUri!.Query);
                var body = statusBodies[Math.Min(_statusCalls, statusBodies.Length - 1)];
                _statusCalls++;
                return Json(HttpStatusCode.OK, body);
            }
            return Json(HttpStatusCode.NotFound, """{"error":{"code":"not_found","message":"no"}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static string Status(string state, int total, int done, int chunks, string? current = null, string errors = "[]") =>
        $$"""
        {"job_id":"job-1","state":"{{state}}","files_total":{{total}},"files_done":{{done}},"chunks_created":{{chunks}},
         "chunks_embedded":0,"current_file":{{(current is null ? "null" : $"\"{current}\"")}},"errors":{{errors}}}
        """;

    private static async Task<MainShellViewModel> CreateAsync(IngestHandler handler)
    {
        var client = new AiServiceClient(new HttpClient(handler), Token);
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-import-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), aiClient: client)
        {
            ManualImportPollInterval = TimeSpan.FromMilliseconds(1),
        };
        await vm.ReloadDatabaseRecordsAsync();
        return vm;
    }

    [Fact]
    public async Task ImportPdfs_SendsPaths_FollowsJobAndListsScannedPageProblems()
    {
        var pageErrors = string.Join(",", Enumerable.Range(1639, 14).Select(p =>
            $$"""{"file":"660-28.pdf","message":"scanned page not searchable: OCR found no readable text","page":{{p}}}"""));
        var handler = new IngestHandler(HttpStatusCode.Accepted, """{"job_id":"job-1"}""",
            Status("pending", 0, 0, 0),
            Status("running", 2, 0, 0, "660-28.pdf"),
            Status("running", 2, 1, 2030, "Ford.pdf"),
            Status("succeeded", 2, 2, 2132, errors: $"[{pageErrors},{{\"file\":\"notes.docx\",\"message\":\"skipped: office format\",\"page\":null}}]"));
        var vm = await CreateAsync(handler);
        var running = new List<bool>();
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsManualImportRunning)) running.Add(vm.IsManualImportRunning);
            if (e.PropertyName == nameof(vm.ManualImportStatusText)) statuses.Add(vm.ManualImportStatusText);
        };
        vm.PickManualFilesAsync = () => Task.FromResult<IReadOnlyList<string>?>(new[] { @"C:\Manuals\660-28.pdf", @"C:\Manuals\Ford.pdf", @"c:\manuals\660-28.PDF" });

        await vm.ImportManualFilesAsync();

        Assert.Equal(1, handler.IngestCalls);
        var paths = handler.IngestBody!["paths"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        Assert.Equal(new[] { @"C:\Manuals\660-28.pdf", @"C:\Manuals\Ford.pdf" }, paths); // same file twice is sent once
        Assert.False(handler.IngestBody!["force_reingest"]!.GetValue<bool>());
        Assert.All(handler.StatusQueries, q => Assert.Equal("?job_id=job-1", q));
        Assert.Equal(4, handler.StatusQueries.Count);
        Assert.Contains("Looking for PDF files…", statuses); // not "0 of 0 files" while the job starts

        Assert.Equal(new[] { true, false }, running);
        Assert.False(vm.IsManualImportRunning);
        Assert.True(vm.ImportManualFilesCommand.CanExecute(null));
        Assert.Contains(statuses, s => s.StartsWith("Importing 1 of 2 file(s) — Ford.pdf. 2030 passage(s) so far.", StringComparison.Ordinal) && s.Contains("(OCR)", StringComparison.Ordinal));
        Assert.Equal("Done: 2 file(s) processed, 2132 searchable passage(s) added. See the list below for what could not be read.",
            vm.ManualImportStatusText);
        Assert.Equal(100, vm.ManualImportProgress);

        Assert.Equal(Visibility.Visible, vm.ManualImportProblemsVisibility);
        Assert.Equal(2, vm.ManualImportProblems.Count); // 14 pages with the same problem are one line
        Assert.Equal(
            "660-28.pdf, pages 1639, 1640, 1641, 1642, 1643, 1644, 1645, 1646, 1647, 1648, 1649, 1650 and 2 more: scanned page not searchable: OCR found no readable text",
            vm.ManualImportProblems[0]);
        Assert.Equal("notes.docx: skipped: office format", vm.ManualImportProblems[1]);
    }

    [Fact]
    public async Task ImportFolder_AlreadyImported_SaysNothingNew()
    {
        var handler = new IngestHandler(HttpStatusCode.Accepted, """{"job_id":"job-1"}""", Status("succeeded", 3, 3, 0));
        var vm = await CreateAsync(handler);
        vm.PickManualFolderAsync = () => Task.FromResult<string?>(@"C:\Manuals\Delta");

        await vm.ImportManualFolderAsync();

        Assert.Equal(@"C:\Manuals\Delta", handler.IngestBody!["paths"]![0]!.GetValue<string>());
        Assert.Equal("Done: 3 file(s) checked. Nothing new: every manual was already in the library.", vm.ManualImportStatusText);
        Assert.Equal(Visibility.Collapsed, vm.ManualImportProblemsVisibility);
    }

    [Fact]
    public async Task CancelledPicker_EmptyFolder_RefusedPath_AndServiceDown()
    {
        var handler = new IngestHandler(HttpStatusCode.Accepted, """{"job_id":"job-1"}""", Status("succeeded", 0, 0, 0));
        var vm = await CreateAsync(handler);

        vm.PickManualFilesAsync = () => Task.FromResult<IReadOnlyList<string>?>(null);
        await vm.ImportManualFilesAsync();
        vm.PickManualFolderAsync = () => Task.FromResult<string?>(null);
        await vm.ImportManualFolderAsync();
        Assert.Equal(0, handler.IngestCalls); // cancelled pickers send nothing

        await vm.ImportManualsAsync(new[] { @"C:\Empty" });
        Assert.Equal("No PDF files were found there.", vm.ManualImportStatusText);

        var refused = new IngestHandler(HttpStatusCode.BadRequest,
            """{"error":{"code":"invalid_request","message":"Network (UNC) paths are not allowed. Copy the manual to a local folder."}}""");
        var vm2 = await CreateAsync(refused);
        await vm2.ImportManualsAsync(new[] { @"\\server\share\manual.pdf" });
        Assert.Equal("Import refused: Network (UNC) paths are not allowed. Copy the manual to a local folder.", vm2.ManualImportStatusText);
        Assert.False(vm2.IsManualImportRunning);

        var down = new AiServiceClient(new HttpClient(new ThrowingHandler()), Token);
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-import-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        var vm3 = new MainShellViewModel(data, new DiagnosticAdapterFactory(), aiClient: down);
        await vm3.ImportManualsAsync(new[] { @"C:\Manuals\660-28.pdf" });
        Assert.StartsWith("AI service is not reachable", vm3.ManualImportStatusText, StringComparison.Ordinal);
        Assert.False(vm3.IsManualImportRunning);
    }

    [Fact]
    public void DescribeProblems_OnePageAndFileLevel()
    {
        var lines = MainShellViewModel.DescribeManualImportProblems(new[]
        {
            new IngestionError { File = "Ford.pdf", Message = "scanned page not searchable: OCR could not read it", Page = 379 },
            new IngestionError { File = "", Message = "boom" },
        });
        Assert.Equal(new[] { "Ford.pdf, page 379: scanned page not searchable: OCR could not read it", "Import: boom" }, lines);
    }

    /// <summary>Answers DELETE /manuals/{id} like the service: soft-deletes the row in the shared database.</summary>
    private sealed class RemoveHandler(Func<DataServices> data, HttpStatusCode status = HttpStatusCode.OK, string? errorBody = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (status != HttpStatusCode.OK)
                return new HttpResponseMessage(status) { Content = new StringContent(errorBody ?? "{}", Encoding.UTF8, "application/json") };
            var id = Uri.UnescapeDataString(request.RequestUri.AbsolutePath["/manuals/".Length..]);
            var manual = await data().Manuals.GetByIdAsync(id, cancellationToken);
            await data().Manuals.SoftDeleteAsync(id, cancellationToken);
            var body = $$"""{"manual_id":"{{id}}","title":"{{manual!.Title}}","chunks_removed":2030}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(MainShellViewModel Vm, DataServices Data)> CreateWithManualsAsync(Func<Func<DataServices>, HttpMessageHandler> handler)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-library-{Guid.NewGuid():N}", "gse.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        await data.Manuals.AddAsync(new Manual
        {
            Id = "m-660", Title = "TUG Model 660-28 Maintenance and Parts Manual", Manufacturer = "TUG", ModelFamily = "660-28",
            FilePath = "manuals/a.pdf", FileHash = "a", PageCount = 1724, ImportedAt = "2026-11-20T12:00:00Z",
        });
        await data.Manuals.AddAsync(new Manual
        {
            Id = "m-ford", Title = "Ford/EDI GCP Diagnostic Support Manual", FilePath = "manuals/b.pdf", FileHash = "b", PageCount = 1,
        });
        var client = new AiServiceClient(new HttpClient(handler(() => data)), Token);
        var vm = new MainShellViewModel(data, new DiagnosticAdapterFactory(), aiClient: client);
        await vm.ReloadDatabaseRecordsAsync();
        return (vm, data);
    }

    [Fact]
    public async Task Library_ListsManuals_RemoveAfterConfirm_DropsItFromListAndSearchHits()
    {
        RemoveHandler? handler = null;
        var (vm, _) = await CreateWithManualsAsync(d => handler = new RemoveHandler(d));

        Assert.Equal("Manual library (2 manuals)", vm.ManualLibraryHeader);
        Assert.Equal(Visibility.Collapsed, vm.NoManualsVisibility);
        Assert.Equal(new[] { "Ford/EDI GCP Diagnostic Support Manual", "TUG Model 660-28 Maintenance and Parts Manual" },
            vm.ManualLibraryRows.Select(r => r.Title));
        Assert.StartsWith("1 page · imported ", vm.ManualLibraryRows[0].Detail, StringComparison.Ordinal); // no maker or model on file
        Assert.StartsWith("TUG · 660-28 · 1724 pages · imported 2026-11-2", vm.ManualLibraryRows[1].Detail, StringComparison.Ordinal);

        vm.ManualSearchResults.Add(new ManualSearchResultViewModel(new SearchResult { ManualId = "m-660", ChunkId = "c1" }, false));
        vm.ManualSearchResults.Add(new ManualSearchResultViewModel(new SearchResult { ManualId = "m-ford", ChunkId = "c2" }, false));
        var row = vm.ManualLibraryRows[1];

        ManualLibraryRowViewModel? asked = null;
        vm.ConfirmRemoveManualAsync = r => { asked = r; return Task.FromResult(false); };
        await vm.RemoveManualAsync(row);
        Assert.Same(row, asked);
        Assert.Empty(handler!.Requests); // "Keep it" sends nothing
        Assert.Equal(2, vm.ManualLibraryRows.Count);

        vm.ConfirmRemoveManualAsync = _ => Task.FromResult(true);
        await vm.RemoveManualAsync(row);
        Assert.Equal(new[] { "DELETE /manuals/m-660" }, handler.Requests);
        Assert.Equal("Manual library (1 manual)", vm.ManualLibraryHeader);
        Assert.Equal("m-ford", Assert.Single(vm.ManualLibraryRows).Id);
        Assert.Equal("m-ford", Assert.Single(vm.ManualSearchResults).ManualId);
        Assert.Equal("Removed \"TUG Model 660-28 Maintenance and Parts Manual\": 2030 passage(s) no longer appear in search or Tech Chat. "
            + "The PDF copy is kept; import it again to bring it back.", vm.ManualLibraryStatusText);
    }

    [Fact]
    public async Task Library_Remove_RefusedBusyAlreadyGoneAndServiceDown()
    {
        var (busy, _) = await CreateWithManualsAsync(_ => new RemoveHandler(() => null!, HttpStatusCode.Conflict,
            """{"error":{"code":"conflict","message":"A manual import is running. Remove the manual when it has finished."}}"""));
        busy.ConfirmRemoveManualAsync = _ => Task.FromResult(true);
        await busy.RemoveManualAsync(busy.ManualLibraryRows[0]);
        Assert.Equal("Could not remove \"Ford/EDI GCP Diagnostic Support Manual\": A manual import is running. Remove the manual when it has finished.",
            busy.ManualLibraryStatusText);
        Assert.Equal(2, busy.ManualLibraryRows.Count);

        var (gone, _) = await CreateWithManualsAsync(_ => new RemoveHandler(() => null!, HttpStatusCode.NotFound,
            """{"error":{"code":"not_found","message":"That manual is not in the library."}}"""));
        gone.ConfirmRemoveManualAsync = _ => Task.FromResult(true);
        await gone.RemoveManualAsync(gone.ManualLibraryRows[0]);
        Assert.Equal("\"Ford/EDI GCP Diagnostic Support Manual\" was already removed.", gone.ManualLibraryStatusText);

        var (down, _) = await CreateWithManualsAsync(_ => new ThrowingHandler());
        down.ConfirmRemoveManualAsync = _ => Task.FromResult(true);
        await down.RemoveManualAsync(down.ManualLibraryRows[0]);
        Assert.StartsWith("AI service is not reachable", down.ManualLibraryStatusText, StringComparison.Ordinal);
        Assert.Equal(2, down.ManualLibraryRows.Count);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
