using System.Net;
using System.Text;
using System.Text.Json;
using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Core.Models;
using GseTechPro.Core.Units;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

public sealed class AiServiceClientAndStateTests
{
    private const string TestToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task HealthAsync_SendsTokenHeader_AndDeserializesSnakeCaseAndEnums()
    {
        HttpRequestMessage? capturedRequest = null;
        var jsonResponse = """
            {
              "status": "degraded",
              "ollama_reachable": false,
              "chat_available": false,
              "search_available": true,
              "chat_model": "custom-chat-model",
              "embedding_model": "custom-embed-model",
              "degradation_reason": "ollama_unreachable",
              "version": "0.3.0"
            }
            """;

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            capturedRequest = req;
            return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, jsonResponse));
        });

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(
            new AiServiceConfig { BaseUrl = "http://127.0.0.1:8765" },
            TestToken,
            httpClient);

        var health = await client.HealthAsync();

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Get, capturedRequest!.Method);
        Assert.Equal("http://127.0.0.1:8765/health", capturedRequest.RequestUri?.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues(AiServiceProtocol.TokenHeader, out var headerValues));
        Assert.Equal(TestToken, Assert.Single(headerValues));

        Assert.Equal(AiServiceStatus.Degraded, health.Status);
        Assert.False(health.OllamaReachable);
        Assert.False(health.ChatAvailable);
        Assert.True(health.SearchAvailable);
        Assert.Equal("custom-chat-model", health.ChatModel);
        Assert.Equal("custom-embed-model", health.EmbeddingModel);
        Assert.Equal("ollama_unreachable", health.DegradationReason);
        Assert.Equal("0.3.0", health.Version);
    }

    [Fact]
    public async Task GetConfigAsync_DeserializesSnakeCase_AndUpdatesEffectiveBaseUrl()
    {
        var jsonResponse = """
            {
              "base_url": "http://127.0.0.1:9001",
              "chat_model": "test-chat-v2",
              "embedding_model": "test-embed-v1",
              "embedding_dimension": 768,
              "idle_unload_minutes": 15
            }
            """;

        var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, jsonResponse)));

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, TestToken, "http://127.0.0.1:8765");

        var config = await client.GetConfigAsync();

        Assert.Equal("http://127.0.0.1:9001", config.BaseUrl);
        Assert.Equal("test-chat-v2", config.ChatModel);
        Assert.Equal("test-embed-v1", config.EmbeddingModel);
        Assert.Equal(768, config.EmbeddingDimension);
        Assert.Equal(15, config.IdleUnloadMinutes);
        Assert.Equal("http://127.0.0.1:9001", client.BaseUrl);
    }

    [Fact]
    public async Task ChatFullAsync_And_ChatAsync_RoundTripSnakeCaseAndCitations()
    {
        string? capturedBody = null;
        string? capturedToken = null;

        var responseJson = """
            {
              "text": "Check the hydraulic pump discharge pressure at port G3.",
              "is_suggestion": true,
              "citations": [
                {
                  "kind": "manual-page",
                  "display_text": "TUG 660-28 Maintenance Manual, page 12",
                  "manual_id": "manual-001",
                  "page_start": 12,
                  "work_order_number": null
                },
                {
                  "kind": "work-order",
                  "display_text": "WO #4471: fixed 18 of 20",
                  "manual_id": null,
                  "page_start": null,
                  "work_order_number": "4471"
                },
                {
                  "kind": "dtc",
                  "display_text": "SPN 94 FMI 3 — Fuel Delivery Pressure",
                  "manual_id": null,
                  "page_start": null,
                  "work_order_number": null
                }
              ]
            }
            """;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.Headers.TryGetValues(AiServiceProtocol.TokenHeader, out var tokens))
            {
                capturedToken = tokens.FirstOrDefault();
            }
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return CreateJsonResponse(HttpStatusCode.OK, responseJson);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, TestToken);

        var chatRequest = new AiChatRequest
        {
            Messages = new[]
            {
                new AiChatMessage
                {
                    Role = AiChatRoles.User,
                    Content = "why no hydraulics?",
                    ToolCallId = null,
                },
            },
            WorkOrderId = "wo-123",
            AssetId = "asset-456",
            UseTools = true,
            SystemInstruction = "Cite sources.",
        };

        var fullResponse = await client.ChatFullAsync(chatRequest);
        var legacyText = await client.ChatAsync(chatRequest);

        Assert.Equal(TestToken, capturedToken);
        Assert.NotNull(capturedBody);

        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        Assert.Equal("wo-123", root.GetProperty("work_order_id").GetString());
        Assert.Equal("asset-456", root.GetProperty("asset_id").GetString());
        Assert.True(root.GetProperty("use_tools").GetBoolean());
        Assert.Equal("Cite sources.", root.GetProperty("system_instruction").GetString());

        var firstMsg = root.GetProperty("messages")[0];
        Assert.Equal("user", firstMsg.GetProperty("role").GetString());
        Assert.Equal("why no hydraulics?", firstMsg.GetProperty("content").GetString());

        Assert.Equal("Check the hydraulic pump discharge pressure at port G3.", fullResponse.Text);
        Assert.Equal(fullResponse.Text, legacyText);
        Assert.True(fullResponse.IsSuggestion);
        Assert.Equal(3, fullResponse.Citations.Count);

        Assert.Equal(CitationKind.ManualPage, fullResponse.Citations[0].Kind);
        Assert.Equal("manual-001", fullResponse.Citations[0].ManualId);
        Assert.Equal(12, fullResponse.Citations[0].PageStart);

        Assert.Equal(CitationKind.WorkOrder, fullResponse.Citations[1].Kind);
        Assert.Equal("4471", fullResponse.Citations[1].WorkOrderNumber);

        Assert.Equal(CitationKind.Dtc, fullResponse.Citations[2].Kind);
    }

    [Fact]
    public async Task SearchManualsFullAsync_And_SearchManualsAsync_RoundTripChunkKindsAndFallbackFlag()
    {
        string? capturedBody = null;
        var responseJson = """
            {
              "results": [
                {
                  "chunk_id": "chunk-guid-1",
                  "manual_id": "manual-uuid-1",
                  "manual_title": "TUG 660-28",
                  "page_start": 12,
                  "page_end": 13,
                  "section": "specs",
                  "text": "Main relief set to ...",
                  "score": 0.87,
                  "citation": "TUG 660-28 Maintenance Manual, page 12"
                }
              ],
              "fallback": true
            }
            """;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return CreateJsonResponse(HttpStatusCode.OK, responseJson);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, TestToken);

        var searchRequest = new SearchManualsRequest
        {
            Query = "hydraulic pressure test port",
            TopK = 5,
            ManualId = "manual-uuid-1",
            ChunkKinds = new[] { ChunkKind.Specs, ChunkKind.DiagramCaption },
            IncludeText = true,
        };

        var full = await client.SearchManualsFullAsync(searchRequest);

        Assert.NotNull(capturedBody);
        using (var doc = JsonDocument.Parse(capturedBody!))
        {
            var root = doc.RootElement;
            Assert.Equal("hydraulic pressure test port", root.GetProperty("query").GetString());
            Assert.Equal(5, root.GetProperty("top_k").GetInt32());
            Assert.Equal("manual-uuid-1", root.GetProperty("manual_id").GetString());
            Assert.True(root.GetProperty("include_text").GetBoolean());

            var kinds = root.GetProperty("chunk_kinds");
            Assert.Equal("specs", kinds[0].GetString());
            Assert.Equal("diagram_caption", kinds[1].GetString());
        }

        Assert.True(full.Fallback);
        var hit = Assert.Single(full.Results);
        Assert.Equal("chunk-guid-1", hit.ChunkId);
        Assert.Equal(12, hit.PageStart);
        Assert.Equal(13, hit.PageEnd);
        Assert.Equal(0.87, hit.Score, 3);

        var legacyHits = await client.SearchManualsAsync("hydraulic pressure test port", 3);
        Assert.Single(legacyHits);
        Assert.Equal("chunk-guid-1", legacyHits[0].ChunkId);
    }

    [Fact]
    public async Task IngestAsync_UnwrapsJobId_And_GetIngestStatusAsync_DeserializesProgress()
    {
        string? capturedPostBody = null;
        Uri? capturedGetUri = null;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.AbsolutePath == AiServiceProtocol.IngestPath)
            {
                capturedPostBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
                return CreateJsonResponse(HttpStatusCode.Accepted, """{ "job_id": "job-uuid-999" }""");
            }

            if (req.Method == HttpMethod.Get && req.RequestUri?.AbsolutePath == AiServiceProtocol.IngestStatusPath)
            {
                capturedGetUri = req.RequestUri;
                var statusJson = """
                    {
                      "job_id": "job-uuid-999",
                      "state": "running",
                      "files_total": 3,
                      "files_done": 1,
                      "chunks_created": 120,
                      "chunks_embedded": 0,
                      "current_file": "tug-660.pdf",
                      "errors": [
                        { "file": "bad.pdf", "message": "unreadable PDF", "page": 4 }
                      ]
                    }
                    """;
                return CreateJsonResponse(HttpStatusCode.OK, statusJson);
            }

            return CreateJsonResponse(HttpStatusCode.NotFound, """{ "error": { "code": "not_found", "message": "Not found", "detail": null } }""");
        });

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, TestToken);

        var jobId = await client.IngestAsync(new IngestRequest
        {
            Paths = new[] { @"C:\manuals\tug-660.pdf" },
            ForceReingest = true,
        });

        Assert.Equal("job-uuid-999", jobId);
        Assert.NotNull(capturedPostBody);
        using (var doc = JsonDocument.Parse(capturedPostBody!))
        {
            Assert.True(doc.RootElement.GetProperty("force_reingest").GetBoolean());
            Assert.Equal(@"C:\manuals\tug-660.pdf", doc.RootElement.GetProperty("paths")[0].GetString());
        }

        var status = await client.GetIngestStatusAsync(jobId);
        Assert.NotNull(capturedGetUri);
        Assert.Equal("?job_id=job-uuid-999", capturedGetUri!.Query);
        Assert.Equal("job-uuid-999", status.JobId);
        Assert.Equal(IngestionState.Running, status.State);
        Assert.Equal(3, status.FilesTotal);
        Assert.Equal(1, status.FilesDone);
        Assert.Equal(120, status.ChunksCreated);
        Assert.Equal(0, status.ChunksEmbedded);
        Assert.Equal("tug-660.pdf", status.CurrentFile);
        var err = Assert.Single(status.Errors);
        Assert.Equal("bad.pdf", err.File);
        Assert.Equal("unreadable PDF", err.Message);
        Assert.Equal(4, err.Page);
    }

    [Fact]
    public async Task GuidedStartAndNextAsync_RoundTripSnakeCaseAndPassFailVerdict()
    {
        string? capturedStartBody = null;
        string? capturedNextBody = null;

        var stepJson = """
            {
              "session_id": "guided-sess-1",
              "available": false,
              "tree_id": null,
              "step_id": null,
              "step_order": 0,
              "instruction": "No troubleshooting tree is available for this model and symptom yet.",
              "question": null,
              "safety_warnings": [
                "WARNING: Relieve stored hydraulic accumulator pressure before opening port G3."
              ],
              "reading_type": "hydraulic_pressure",
              "test_point_id": null,
              "spec_min": null,
              "spec_max": null,
              "spec_source": null,
              "terminal_result": null,
              "next_on_pass": null,
              "next_on_fail": null
            }
            """;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.GuidedStartPath)
            {
                capturedStartBody = await req.Content!.ReadAsStringAsync(ct);
            }
            else if (req.RequestUri?.AbsolutePath == AiServiceProtocol.GuidedNextPath)
            {
                capturedNextBody = await req.Content!.ReadAsStringAsync(ct);
            }
            return CreateJsonResponse(HttpStatusCode.OK, stepJson);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, TestToken);

        var startStep = await client.StartGuidedAsync(new GuidedStartRequest
        {
            EquipmentModelId = "TUG-660-28",
            SymptomId = "sym-1",
            ComplaintText = "Slow lift",
        });

        Assert.NotNull(capturedStartBody);
        using (var doc = JsonDocument.Parse(capturedStartBody!))
        {
            Assert.Equal("TUG-660-28", doc.RootElement.GetProperty("equipment_model_id").GetString());
            Assert.Equal("sym-1", doc.RootElement.GetProperty("symptom_id").GetString());
            Assert.Equal("Slow lift", doc.RootElement.GetProperty("complaint_text").GetString());
        }

        Assert.Equal("guided-sess-1", startStep.SessionId);
        Assert.False(startStep.Available);
        Assert.Equal(ReadingType.HydraulicPressure, startStep.ReadingType);
        Assert.Single(startStep.SafetyWarnings);

        var nextStep = await client.NextGuidedAsync(new GuidedNextRequest
        {
            SessionId = "guided-sess-1",
            StepId = "step-01",
            Verdict = PassFail.NotApplicable,
            ReadingValue = 1850.5,
            ReadingUnit = "psi",
            Answer = "Gauge connected",
        });

        Assert.NotNull(capturedNextBody);
        using (var doc = JsonDocument.Parse(capturedNextBody!))
        {
            Assert.Equal("guided-sess-1", doc.RootElement.GetProperty("session_id").GetString());
            Assert.Equal("step-01", doc.RootElement.GetProperty("step_id").GetString());
            Assert.Equal("na", doc.RootElement.GetProperty("verdict").GetString());
            Assert.Equal(1850.5, doc.RootElement.GetProperty("reading_value").GetDouble());
            Assert.Equal("psi", doc.RootElement.GetProperty("reading_unit").GetString());
        }

        Assert.Equal("guided-sess-1", nextStep.SessionId);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiErrorCodes.AiUnavailable, "AI assistant is unavailable (Ollama not reachable).", "ollama_unreachable", true, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiErrorCodes.AiStarting, "Model warming up.", "loading model", false, true)]
    [InlineData(HttpStatusCode.Unauthorized, AiErrorCodes.Unauthorized, "Invalid or missing X-GSE-Token.", null, false, false)]
    [InlineData(HttpStatusCode.NotFound, AiErrorCodes.NotFound, "Job not found.", "job_id=missing", false, false)]
    public async Task ErrorEnvelope_Non2xx_UnwrapsIntoAiServiceException(
        HttpStatusCode statusCode,
        string errorCode,
        string message,
        string? detail,
        bool expectedIsUnavailable,
        bool expectedIsStarting)
    {
        var envelope = new AiErrorEnvelope
        {
            Error = new AiError
            {
                Code = errorCode,
                Message = message,
                Detail = detail,
            },
        };
        var body = JsonSerializer.Serialize(envelope, AiServiceJson.Options);

        var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(CreateJsonResponse(statusCode, body)));

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, TestToken);

        var ex = await Assert.ThrowsAsync<AiServiceException>(() =>
            client.ChatFullAsync(new AiChatRequest
            {
                Messages = new[] { new AiChatMessage { Role = AiChatRoles.User, Content = "test" } },
            }));

        Assert.Equal(statusCode, ex.StatusCode);
        Assert.Equal(errorCode, ex.Code);
        Assert.Equal(message, ex.Error.Message);
        Assert.Equal(detail, ex.Detail);
        Assert.Equal(expectedIsUnavailable, ex.IsAiUnavailable);
        Assert.Equal(expectedIsStarting, ex.IsAiStarting);
    }

    [Fact]
    public async Task AiServiceStateService_MapsDegradedStartingReadyAndUnavailableStates_AndReadsModelFromConfig()
    {
        var currentHealth = new AiServiceHealth
        {
            Status = AiServiceStatus.Degraded,
            OllamaReachable = false,
            ChatAvailable = false,
            SearchAvailable = true,
            ChatModel = "config-driven-chat-model",
            EmbeddingModel = "config-driven-embed-model",
            DegradationReason = "ollama_unreachable",
            Version = "0.3.0",
        };

        var currentConfig = new AiServiceConfig
        {
            BaseUrl = "http://127.0.0.1:8765",
            ChatModel = "config-driven-chat-model",
            EmbeddingModel = "config-driven-embed-model",
            EmbeddingDimension = 768,
            IdleUnloadMinutes = 10,
        };

        bool throwUnreachable = false;

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            if (throwUnreachable)
            {
                throw new HttpRequestException("Connection refused");
            }

            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.HealthPath)
            {
                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(currentHealth, AiServiceJson.Options)));
            }

            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.ConfigPath)
            {
                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(currentConfig, AiServiceJson.Options)));
            }

            return Task.FromResult(CreateJsonResponse(HttpStatusCode.NotFound, "{}"));
        });

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, TestToken);
        await using var stateService = new AiServiceStateService(client);

        // 1. Degraded (no Ollama, search available)
        await stateService.RefreshOnceAsync();
        Assert.Equal(AiUiState.DegradedSearch, stateService.UiState);
        Assert.False(stateService.IsChatAvailable);
        Assert.True(stateService.IsSearchAvailable);
        Assert.Equal("config-driven-chat-model", stateService.ChatModel);
        Assert.Equal("config-driven-embed-model", stateService.EmbeddingModel);
        Assert.Equal("AI: Degraded (Search Only)", stateService.StatusIndicatorText);
        Assert.Contains("Ollama", stateService.StatusBannerText);

        // 2. Starting / Warming up
        currentHealth = currentHealth with
        {
            Status = AiServiceStatus.Starting,
            OllamaReachable = true,
            ChatAvailable = false,
            DegradationReason = AiErrorCodes.AiStarting,
        };
        await stateService.RefreshOnceAsync();
        Assert.Equal(AiUiState.WarmingUp, stateService.UiState);
        Assert.Equal("AI: Warming up…", stateService.StatusIndicatorText);
        Assert.Contains("AI is starting", stateService.StatusBannerText);

        // 3. Ready (ok + chat_available: true)
        currentHealth = currentHealth with
        {
            Status = AiServiceStatus.Ok,
            OllamaReachable = true,
            ChatAvailable = true,
            SearchAvailable = true,
            DegradationReason = null,
        };
        await stateService.RefreshOnceAsync();
        Assert.Equal(AiUiState.Ready, stateService.UiState);
        Assert.True(stateService.IsChatAvailable);
        Assert.Equal("AI: Ready", stateService.StatusIndicatorText);

        // 4. Service unreachable -> Unavailable
        throwUnreachable = true;
        await stateService.RefreshOnceAsync();
        Assert.Equal(AiUiState.Unavailable, stateService.UiState);
        Assert.False(stateService.IsChatAvailable);
        Assert.False(stateService.IsSearchAvailable);
        Assert.Equal("AI: Unavailable", stateService.StatusIndicatorText);
    }

    [Fact]
    public async Task MainShellViewModel_TechChat_Handles503Degraded_And_RendersCitationsAndGuidedFallback()
    {
        bool return503 = true;

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.ChatPath)
            {
                if (return503)
                {
                    var err = new AiErrorEnvelope
                    {
                        Error = new AiError
                        {
                            Code = AiErrorCodes.AiUnavailable,
                            Message = "AI assistant is unavailable (Ollama not reachable). Manual search remains available.",
                            Detail = "ollama_unreachable",
                        },
                    };
                    return Task.FromResult(CreateJsonResponse(
                        HttpStatusCode.ServiceUnavailable,
                        JsonSerializer.Serialize(err, AiServiceJson.Options)));
                }

                var chatResp = new ChatResponse
                {
                    Text = "Inspect relief valve on page 12.",
                    IsSuggestion = true,
                    Citations = new[]
                    {
                        new Citation
                        {
                            Kind = CitationKind.ManualPage,
                            DisplayText = "TUG 660-28 Manual, page 12",
                            ManualId = "unimported-manual-id",
                            PageStart = 12,
                        },
                        new Citation
                        {
                            Kind = CitationKind.WorkOrder,
                            DisplayText = "WO #4471: fixed 18 of 20",
                            WorkOrderNumber = "4471",
                        },
                    },
                };
                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(chatResp, AiServiceJson.Options)));
            }

            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.GuidedStartPath)
            {
                var step = new GuidedStep
                {
                    SessionId = "sess-42",
                    Available = false,
                    Instruction = "No troubleshooting tree is available for this model and symptom yet.",
                };
                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(step, AiServiceJson.Options)));
            }

            return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, "{}"));
        });

        using var httpClient = new HttpClient(handler);
        using var aiClient = new AiServiceClient(httpClient, TestToken);
        var dataServices = new DataServices(new DatabaseOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"gse-ui-test-{Guid.NewGuid():N}.db"),
        });
        var adapterFactory = new DiagnosticAdapterFactory();
        await using var vm = new MainShellViewModel(
            dataServices,
            adapterFactory,
            dispatcherQueue: null,
            aiClient: aiClient);

        // 1. Degraded 503 /chat response -> appends graceful degraded notice instead of throwing
        vm.SelectToolbarAction("AiTechChat");
        vm.ChatInputText = "Why is hydraulic pressure low?";
        await vm.SendChatMessageAsync();

        Assert.Contains(vm.ChatMessages, m => m.IsDegradedNotice && m.Content.Contains("AI unavailable"));
        Assert.Equal(AiUiState.Unavailable, vm.AiState.UiState);

        // 2. Successful /chat response -> preserves IsSuggestion == true & tappable Citations
        return503 = false;
        vm.ChatInputText = "Show port G3 spec";
        await vm.SendChatMessageAsync();

        var assistantMsg = vm.ChatMessages.Last(m => m.Role == AiChatRoles.Assistant);
        Assert.True(assistantMsg.IsSuggestion);
        Assert.Equal(2, assistantMsg.Citations.Count);

        // Tapping unimported manual citation shows "Manual, page 12"
        await vm.OpenCitationAsync(assistantMsg.Citations[0]);
        Assert.Contains("Manual, page 12", vm.CitationActionBanner);

        // Tapping work-order citation opens/selects WO #4471 tab
        await vm.OpenCitationAsync(assistantMsg.Citations[1]);
        Assert.NotNull(vm.SelectedTab);
        Assert.Contains("4471", vm.SelectedTab!.Title);

        // 3. Guided troubleshooting skeleton -> Available == false handled cleanly
        await vm.StartGuidedTroubleshootingAsync();
        Assert.NotNull(vm.CurrentGuidedStep);
        Assert.False(vm.CurrentGuidedStep!.Available);
        Assert.Equal("No troubleshooting tree is available for this model and symptom yet.", vm.GuidedInstructionText);
    }

    [Fact]
    public async Task AiServiceLauncher_GeneratesPerInstallToken_AndFallsBackToDevLocalTokenOn401()
    {
        var token = AiServiceLauncher.GenerateInstallToken();
        Assert.Equal(AiServiceProtocol.TokenHexLength, token.Length);
        Assert.Matches("^[0-9a-f]{64}$", token);

        var launcher = new AiServiceLauncher(port: 8765, token: null);
        Assert.Contains("--port 8765 --token ", launcher.BuildLaunchArguments());

        int callCount = 0;
        var handler = new MockHttpMessageHandler((req, _) =>
        {
            callCount++;
            req.Headers.TryGetValues(AiServiceProtocol.TokenHeader, out var values);
            var sentToken = values?.FirstOrDefault();

            if (sentToken != AiServiceLauncher.StandaloneDevToken)
            {
                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.Unauthorized,
                    """{ "error": { "code": "unauthorized", "message": "Invalid token", "detail": null } }"""));
            }

            return Task.FromResult(CreateJsonResponse(
                HttpStatusCode.OK,
                """{ "status": "degraded", "ollama_reachable": false, "chat_available": false, "search_available": true, "chat_model": "model-a", "embedding_model": "model-b", "degradation_reason": "ollama_unreachable", "version": "0.3.0" }"""));
        });

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, launcher.Token, launcher.BaseUrl);

        var reachable = await launcher.ProbeExistingServiceAsync(client);
        Assert.True(reachable);
        Assert.True(launcher.IsServiceReachable);
        Assert.Equal(2, callCount);
        Assert.Equal(AiServiceLauncher.StandaloneDevToken, client.Token);

        // When allowDevTokenFallback is false (Release mode default), 401 does NOT fall back to dev-local-token
        var releaseLauncher = new AiServiceLauncher(port: 8765, token: null, allowDevTokenFallback: false);
        int releaseCalls = 0;
        var releaseHandler = new MockHttpMessageHandler((_, _) =>
        {
            releaseCalls++;
            return Task.FromResult(CreateJsonResponse(
                HttpStatusCode.Unauthorized,
                """{ "error": { "code": "unauthorized", "message": "Invalid token", "detail": null } }"""));
        });
        using var releaseHttp = new HttpClient(releaseHandler);
        using var releaseClient = new AiServiceClient(releaseHttp, releaseLauncher.Token, releaseLauncher.BaseUrl);

        var releaseReachable = await releaseLauncher.ProbeExistingServiceAsync(releaseClient);
        Assert.False(releaseReachable);
        Assert.False(releaseLauncher.IsServiceReachable);
        Assert.Equal(1, releaseCalls);
    }

    [Fact]
    public void AiServiceClient_DefaultTimeoutIs130Seconds_And_ApplyConfigRejectsNonLoopbackHost()
    {
        Assert.Equal(TimeSpan.FromSeconds(130), AiServiceClient.DefaultTimeout);

        Assert.Equal("http://127.0.0.1:8765", AiServiceClient.NormalizeBaseUrl("http://127.0.0.1:8765/"));
        Assert.Equal("http://localhost:8765", AiServiceClient.NormalizeBaseUrl("localhost:8765"));

        Assert.Throws<ArgumentException>(() => AiServiceClient.NormalizeBaseUrl("http://0.0.0.0:8765"));
        Assert.Throws<ArgumentException>(() => AiServiceClient.NormalizeBaseUrl("http://192.168.1.50:8765"));
        Assert.Throws<ArgumentException>(() => AiServiceClient.NormalizeBaseUrl("https://example.com:8765"));

        using var client = new AiServiceClient();
        Assert.Throws<ArgumentException>(() => client.ApplyConfig(new AiServiceConfig
        {
            BaseUrl = "http://0.0.0.0:8765",
        }));
    }

    [Fact]
    public async Task AiServiceStateService_DoesNotDoubleProbeWhenUnreachable_BacksOffPolling_AndRefreshesHealthOnChatTimeout()
    {
        int healthRequests = 0;
        bool serviceOnline = false;

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.HealthPath)
            {
                healthRequests++;
                if (!serviceOnline)
                {
                    throw new HttpRequestException("Connection refused");
                }

                var health = new AiServiceHealth
                {
                    Status = AiServiceStatus.Starting,
                    OllamaReachable = true,
                    ChatAvailable = false,
                    SearchAvailable = true,
                    ChatModel = "cold-load-model",
                    EmbeddingModel = "embed-model",
                    DegradationReason = AiErrorCodes.AiStarting,
                    Version = "0.3.0",
                };
                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(health, AiServiceJson.Options)));
            }

            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.ConfigPath)
            {
                var cfg = new AiServiceConfig
                {
                    BaseUrl = "http://127.0.0.1:8765",
                    ChatModel = "cold-load-model",
                    EmbeddingModel = "embed-model",
                };
                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(cfg, AiServiceJson.Options)));
            }

            return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, "{}"));
        });

        using var httpClient = new HttpClient(handler);
        var launcher = new AiServiceLauncher(port: 8765, token: TestToken);
        using var client = new AiServiceClient(httpClient, launcher.Token, launcher.BaseUrl);
        await using var stateService = new AiServiceStateService(client, launcher);

        // 1. When ProbeExistingServiceAsync returns false, RefreshOnceAsync returns immediately (only 1 /health request)
        await stateService.RefreshOnceAsync();
        Assert.Equal(1, healthRequests);
        Assert.Equal(AiUiState.Unavailable, stateService.UiState);
        Assert.Equal(1, stateService.ConsecutiveUnreachableCount);
        Assert.Equal(AiServiceStateService.DefaultPollInterval, stateService.CurrentPollDelay);

        // 2. After 3 consecutive unreachable polls, CurrentPollDelay backs off from 10 s to 30 s
        await stateService.RefreshOnceAsync();
        await stateService.RefreshOnceAsync();
        Assert.Equal(3, healthRequests);
        Assert.Equal(3, stateService.ConsecutiveUnreachableCount);
        Assert.Equal(AiServiceStateService.BackoffPollInterval, stateService.CurrentPollDelay);

        // 3. Service comes online; next RefreshOnceAsync succeeds, resets backoff, and marks launcher reachable
        serviceOnline = true;
        await stateService.RefreshOnceAsync();
        Assert.True(launcher.IsServiceReachable);
        Assert.Equal(0, stateService.ConsecutiveUnreachableCount);
        Assert.Equal(AiServiceStateService.DefaultPollInterval, stateService.CurrentPollDelay);
        Assert.Equal(AiUiState.WarmingUp, stateService.UiState);

        // 4. On a /chat TaskCanceledException (slow CPU model load), HandleChatCallFailureAsync refreshes /health
        //    rather than immediately marking the service Unavailable
        int beforeTimeoutHealthRequests = healthRequests;
        await stateService.HandleChatCallFailureAsync(new TaskCanceledException("Chat timed out at 130s"));
        Assert.Equal(beforeTimeoutHealthRequests + 1, healthRequests);
        Assert.Equal(AiUiState.WarmingUp, stateService.UiState);
        Assert.True(launcher.IsServiceReachable);

        // 5. When connection fails again, launcher reachability is reset
        serviceOnline = false;
        await stateService.RefreshOnceAsync();
        Assert.False(launcher.IsServiceReachable);
        Assert.Equal(AiUiState.Unavailable, stateService.UiState);
    }

    [Fact]
    public async Task Phase4_GuidedWalker_FormatsDisplaySpecs_RequiresWorkOrder_PersistsCanonicalReadingBeforeNext_AndHandlesTodoAndTerminal()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-phase4-guided-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();

        var step1CanonicalMin = UnitConverter.ToCanonical(2300.0, UnitConverter.Psi);
        var step1CanonicalMax = UnitConverter.ToCanonical(2500.0, UnitConverter.Psi);

        GuidedNextRequest? capturedNextRequest1 = null;
        GuidedNextRequest? capturedNextRequest2 = null;
        int readingsCountDuringFirstNextCall = -1;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.GuidedStartPath)
            {
                var step1 = new GuidedStep
                {
                    SessionId = "sess-p4-1",
                    Available = true,
                    TreeId = "tree-tug66028-hyd",
                    StepId = "step-hyd-01",
                    StepOrder = 1,
                    Instruction = "Connect calibrated gauge to main relief test port.",
                    Question = "Is the main relief pressure within specification?",
                    SafetyWarnings = new[]
                    {
                        "Hydraulic oil and raised components can retain energy after shutdown.",
                    },
                    ReadingType = ReadingType.HydraulicPressure,
                    TestPointId = "tp-tug66028-main-relief",
                    SpecMin = step1CanonicalMin,
                    SpecMax = step1CanonicalMax,
                    SpecSource = "TUG Model 660-28 Operation Manual CD397-Oper, page 101",
                    NextOnPass = "step-hyd-02",
                    NextOnFail = "step-hyd-fail",
                };
                return CreateJsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(step1, AiServiceJson.Options));
            }

            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.GuidedNextPath)
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                var nextReq = JsonSerializer.Deserialize<GuidedNextRequest>(body, AiServiceJson.Options)!;

                if (capturedNextRequest1 is null)
                {
                    capturedNextRequest1 = nextReq;
                    var existingReadings = await dataServices.Readings.GetAllAsync(ct);
                    readingsCountDuringFirstNextCall = existingReadings.Count;

                    // Return a Step 2 with TODO spec (null SpecMin/SpecMax)
                    var step2 = new GuidedStep
                    {
                        SessionId = "sess-p4-1",
                        Available = true,
                        TreeId = "tree-tug66028-hyd",
                        StepId = "step-hyd-02",
                        StepOrder = 2,
                        Instruction = "Measure battery open-circuit resting voltage.",
                        Question = "Does resting voltage meet manual criterion?",
                        SafetyWarnings = new[] { "Battery systems are high-current energy sources." },
                        ReadingType = ReadingType.Voltage,
                        TestPointId = "tp-tug66028-battery",
                        SpecMin = null,
                        SpecMax = null,
                        SpecSource = "TODO: needs spec from manual",
                        NextOnPass = "step-hyd-03",
                        NextOnFail = "step-hyd-03",
                    };
                    return CreateJsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(step2, AiServiceJson.Options));
                }

                capturedNextRequest2 = nextReq;
                var terminalStep = new GuidedStep
                {
                    SessionId = "sess-p4-1",
                    Available = true,
                    TreeId = "tree-tug66028-hyd",
                    StepId = "step-hyd-03",
                    StepOrder = 3,
                    Instruction = "Isolate energy before repair.",
                    SafetyWarnings = new[] { "Apply lockout/tagout and verify zero energy." },
                    TerminalResult = "Inspect and clean main relief valve cartridge per manual page 101.",
                };
                return CreateJsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(terminalStep, AiServiceJson.Options));
            }

            return CreateJsonResponse(HttpStatusCode.OK, "{}");
        });

        using var httpClient = new HttpClient(handler);
        using var aiClient = new AiServiceClient(httpClient, TestToken);
        var adapterFactory = new DiagnosticAdapterFactory();
        await using var vm = new MainShellViewModel(
            dataServices,
            adapterFactory,
            dispatcherQueue: null,
            aiClient: aiClient);

        await vm.ReloadDatabaseRecordsAsync();

        // 1. Catalog is populated from seeded DB: eqm-tug-660-28 and eqm-tug-600i as Belt Loaders, no stale IDs
        Assert.Contains(vm.GuidedEquipmentModels, m => m.Id == "eqm-tug-660-28" && m.DisplayLabel.Contains("Belt Loader"));
        Assert.Contains(vm.GuidedEquipmentModels, m => m.Id == "eqm-tug-600i" && m.DisplayLabel.Contains("Belt Loader"));
        Assert.DoesNotContain(vm.GuidedEquipmentModels, m => m.Id is "TUG-MA-50" or "TLD-TMX-150" or "HOBART-2400");
        Assert.DoesNotContain(vm.GuidedEquipmentModels, m => m.DisplayLabel.Contains("tow tractor", StringComparison.OrdinalIgnoreCase));

        // 2. Start guided session -> Step 1 has canonical Pa spec, displayed in Imperial psi
        await vm.StartGuidedTroubleshootingAsync();
        Assert.NotNull(vm.CurrentGuidedStep);
        Assert.True(vm.CurrentGuidedStep!.Available);
        Assert.Equal("Expected: 2,300 – 2,500 psi (TUG Model 660-28 Operation Manual CD397-Oper, page 101)", vm.GuidedSpecSummaryText);

        // Switching to Metric converts display spec to bar while keeping canonical SI untouched
        vm.ToggleUnitSystem();
        Assert.Contains("bar", vm.GuidedSpecSummaryText);
        vm.ToggleUnitSystem();
        Assert.Contains("2,300 – 2,500 psi", vm.GuidedSpecSummaryText);

        // 3. Without an open work order, NextGuidedStepAsync shows friendly "open a work order first" and does NOT advance
        vm.GuidedReadingValue = 2400.0;
        vm.GuidedReadingUnit = UnitConverter.Psi;
        await vm.NextGuidedStepAsync(null);

        Assert.Contains("open a work order first", vm.GuidedStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(capturedNextRequest1);
        Assert.Equal("step-hyd-01", vm.CurrentGuidedStep.StepId);
        Assert.Empty(await dataServices.Readings.GetAllAsync());

        // 4. Open/create an active work order and advance with a passing reading (2400 psi)
        await vm.CreateActiveWorkOrderAsync();
        Assert.True(vm.HasActiveWorkOrder);
        var activeWoId = vm.ActiveWorkOrder!.Id;

        await vm.NextGuidedStepAsync(null);

        // Verify reading was persisted to SQLite BEFORE /guided/next was invoked
        Assert.Equal(1, readingsCountDuringFirstNextCall);
        Assert.NotNull(capturedNextRequest1);
        Assert.Equal(PassFail.Pass, capturedNextRequest1!.Verdict);
        Assert.Equal(2400.0, capturedNextRequest1.ReadingValue);
        Assert.Equal("psi", capturedNextRequest1.ReadingUnit);

        var savedReadings = await dataServices.Readings.GetAllAsync();
        var firstReading = Assert.Single(savedReadings);
        Assert.Equal(activeWoId, firstReading.WorkOrderId);
        Assert.Equal("tp-tug66028-main-relief", firstReading.TestPointId);
        Assert.Equal(ReadingType.HydraulicPressure, firstReading.ReadingType);
        Assert.Equal(UnitConverter.ToCanonical(2400.0, UnitConverter.Psi), firstReading.ValueCanonical, 3);
        Assert.Equal(UnitConverter.Pascal, firstReading.UnitCanonical);
        Assert.Equal(2400.0, firstReading.EnteredValue);
        Assert.Equal(UnitConverter.Psi, firstReading.EnteredUnit);
        Assert.Equal(step1CanonicalMin, firstReading.SpecMin!.Value, 3);
        Assert.Equal(step1CanonicalMax, firstReading.SpecMax!.Value, 3);
        Assert.Equal(UnitConverter.Pascal, firstReading.SpecUnit);
        Assert.Equal(PassFail.Pass, firstReading.PassFail);
        Assert.Equal(ReadingSource.Manual, firstReading.Source);
        Assert.False(firstReading.IsSimulated);

        // 5. Step 2 has TODO spec (null SpecMin/SpecMax) -> shows source only, IsGuidedSpecTodo == true, computes NotApplicable
        Assert.Equal("step-hyd-02", vm.CurrentGuidedStep.StepId);
        Assert.Equal("TODO: needs spec from manual", vm.GuidedSpecSummaryText);
        Assert.True(vm.IsGuidedSpecTodo);
        Assert.Equal(Visibility.Visible, vm.GuidedSpecTodoVisibility);
        Assert.Equal(UnitConverter.Volt, vm.GuidedReadingUnit);

        vm.GuidedReadingValue = 12.65;
        vm.GuidedReadingUnit = UnitConverter.Volt;
        await vm.NextGuidedStepAsync(null);

        Assert.NotNull(capturedNextRequest2);
        Assert.Equal(PassFail.NotApplicable, capturedNextRequest2!.Verdict);

        var allReadingsAfterStep2 = await dataServices.Readings.GetAllAsync();
        Assert.Equal(2, allReadingsAfterStep2.Count);
        var todoReading = allReadingsAfterStep2.Single(r => r.TestPointId == "tp-tug66028-battery");
        Assert.Equal(PassFail.NotApplicable, todoReading.PassFail);
        Assert.Null(todoReading.SpecMin);
        Assert.Null(todoReading.SpecMax);
        Assert.Equal("TODO: needs spec from manual", todoReading.SpecSource);
        Assert.False(todoReading.IsSimulated);

        // 6. Step 3 is a terminal step -> shows TerminalResult + "AI/tree suggestion — technician makes the final call"
        Assert.Equal("step-hyd-03", vm.CurrentGuidedStep.StepId);
        Assert.True(vm.HasGuidedTerminalResult);
        Assert.Equal(Visibility.Visible, vm.GuidedTerminalResultVisibility);
        Assert.Contains("Inspect and clean main relief valve", vm.GuidedTerminalResultText);
        Assert.Equal("AI/tree suggestion — technician makes the final call", vm.GuidedTerminalSuggestionLabel);
    }

    [Fact]
    public async Task Phase4_ManualReadingsScreen_FiltersTestPoints_ResolvesSafetyWarnings_ComputesPassFail_SavesReading_AndBadgesSimulated()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-phase4-readings-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();

        var adapterFactory = new DiagnosticAdapterFactory();
        await using var vm = new MainShellViewModel(dataServices, adapterFactory);
        await vm.ReloadDatabaseRecordsAsync();

        // 1. Nav rail contains Readings item and SelectNavRailItem("Readings") activates the screen
        Assert.Contains(vm.NavRailItems, item => item.Key == "Readings" && item.Glyph == "\uE8E5");
        await vm.SelectNavRailItem("Readings");
        Assert.True(vm.IsReadingsViewVisible);
        Assert.Equal(Visibility.Visible, vm.ReadingsViewVisibility);

        // 2. Filter test points by eqm-tug-660-28 vs eqm-tug-600i
        vm.FilterTestPointsByModel("eqm-tug-660-28");
        var tug660Points = vm.FilteredTestPoints.Where(x => !x.IsFreeEntry).ToList();
        Assert.NotEmpty(tug660Points);
        Assert.All(tug660Points, x => Assert.Equal("eqm-tug-660-28", x.EquipmentModelId));

        vm.FilterTestPointsByModel("eqm-tug-600i");
        var tug600iPoints = vm.FilteredTestPoints.Where(x => !x.IsFreeEntry).ToList();
        Assert.NotEmpty(tug600iPoints);
        Assert.All(tug600iPoints, x => Assert.Equal("eqm-tug-600i", x.EquipmentModelId));

        // 3. Select TUG 660-28 Main hydraulic relief test point (1,500 psi spec)
        vm.FilterTestPointsByModel("eqm-tug-660-28");
        var mainReliefItem = vm.FilteredTestPoints.First(x => x.Id == "tp-tug66028-main-relief");
        vm.SelectedTestPointItem = mainReliefItem;

        Assert.False(vm.IsFreeEntryReadingMode);
        Assert.Equal("Main hydraulic relief pressure setting", vm.SelectedTestPointNameDisplay);
        Assert.Equal(UnitConverter.Psi, vm.ManualReadingUnit);
        Assert.Contains("CD397-Oper", vm.SelectedTestPointCitationDisplay);

        // Safety warnings resolved in ADR-009 severity order (hydraulic_pressure first, then lockout_tagout, etc.)
        Assert.NotEmpty(vm.ActiveTestPointSafetyWarnings);
        Assert.Equal(Visibility.Visible, vm.ActiveTestPointSafetyWarningsVisibility);
        Assert.Equal(SafetyWarningCategory.HydraulicPressure, vm.ActiveTestPointSafetyWarnings[0].Category);
        Assert.Equal(SafetyWarningCategory.LockoutTagout, vm.ActiveTestPointSafetyWarnings[1].Category);

        // 4. Attempting to save without an open work order shows friendly message and does not save
        vm.ManualReadingValue = 1500.0;
        await vm.SaveManualReadingAsync();
        Assert.Contains("open a work order first", vm.ManualReadingStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.WorkOrderReadings);

        // 5. Open a work order:
        //    5a. Single-value spec (tp-tug66028-main-relief: spec_min == spec_max == 1500 psi) -> yields PassFail.NotApplicable (ADR-009 §5)
        await vm.CreateActiveWorkOrderAsync();

        vm.ManualReadingValue = 1500.0;
        vm.ManualReadingUnit = UnitConverter.Psi;
        Assert.Equal(PassFail.NotApplicable, vm.ManualReadingComputedPassFail);
        Assert.Contains("N/A", vm.ManualReadingVerdictLabel);
        await vm.SaveManualReadingAsync();

        //    5b. Real range test point (2300 – 2500 psi) -> yields Pass (2400 psi) and Fail (1800 psi)
        var rangedTestPoint = new TestPoint
        {
            Id = "tp-test-ranged-hyd",
            EquipmentModelId = "eqm-tug-660-28",
            Name = "Auxiliary hydraulic circuit regulated range",
            System = "Hydraulic",
            Location = "Test port G4",
            ReadingType = ReadingType.HydraulicPressure,
            ExpectedUnit = UnitConverter.Psi,
            SpecMin = 2300.0,
            SpecMax = 2500.0,
            SpecSource = "Test Manual, page 42",
            ProcedureRef = "Connect calibrated gauge.",
            SafetyWarningIds = "[\"sw-hydraulic-stored-pressure\"]",
        };
        await dataServices.TestPoints.AddAsync(rangedTestPoint);
        vm.SelectedTestPoint = rangedTestPoint;

        vm.ManualReadingValue = 2400.0;
        vm.ManualReadingUnit = UnitConverter.Psi;
        Assert.Equal(PassFail.Pass, vm.ManualReadingComputedPassFail);
        Assert.Contains("PASS", vm.ManualReadingVerdictLabel);
        await vm.SaveManualReadingAsync();

        vm.ManualReadingValue = 1800.0;
        vm.ManualReadingUnit = UnitConverter.Psi;
        Assert.Equal(PassFail.Fail, vm.ManualReadingComputedPassFail);
        Assert.Contains("FAIL", vm.ManualReadingVerdictLabel);
        await vm.SaveManualReadingAsync();

        // 6. Select a null-spec test point (tp-tug66028-battery) -> shows TODO and computes PassFail.NotApplicable
        var batteryItem = vm.FilteredTestPoints.First(x => x.Id == "tp-tug66028-battery");
        vm.SelectedTestPointItem = batteryItem;
        Assert.Equal(UnitConverter.Volt, vm.ManualReadingUnit);
        Assert.Equal("TODO: needs spec from manual", vm.SelectedTestPointSpecDisplay);
        vm.ManualReadingValue = 12.6;
        Assert.Equal(PassFail.NotApplicable, vm.ManualReadingComputedPassFail);
        Assert.Contains("TODO: needs spec from manual", vm.ManualReadingVerdictLabel);
        await vm.SaveManualReadingAsync();

        // 7. Free-entry mode (no test point)
        vm.EnableFreeEntryReadingMode();
        Assert.True(vm.IsFreeEntryReadingMode);
        Assert.Null(vm.SelectedTestPoint);
        vm.ManualReadingType = ReadingType.Temperature;
        vm.ManualChannelComponent = "Hydraulic reservoir return line";
        vm.ManualReadingValue = 140.0;
        vm.ManualReadingUnit = UnitConverter.Fahrenheit;
        Assert.Equal(60.0, vm.ManualReadingCanonicalValue, 3);
        Assert.Equal(UnitConverter.Celsius, vm.ManualReadingCanonicalUnit);
        Assert.Equal(PassFail.NotApplicable, vm.ManualReadingComputedPassFail);
        await vm.SaveManualReadingAsync();

        Assert.Equal(5, vm.WorkOrderReadings.Count);
        Assert.All(vm.WorkOrderReadings, row =>
        {
            Assert.False(row.IsSimulated);
            Assert.Equal(Visibility.Collapsed, row.SimulatedBadgeVisibility);
        });

        // Verify saved single-value spec reading has PassFail == NotApplicable
        var savedMainRelief = Assert.Single(vm.WorkOrderReadings, r => r.TestPointId == "tp-tug66028-main-relief");
        Assert.Equal(PassFail.NotApplicable, savedMainRelief.PassFail);

        // 8. Verify SIMULATED badge wiring when Reading.IsSimulated == true
        var simReading = new Reading
        {
            WorkOrderId = vm.ActiveWorkOrder!.Id,
            ReadingType = ReadingType.Voltage,
            ChannelComponent = "Simulated Replay Channel",
            ValueCanonical = 13.8,
            UnitCanonical = UnitConverter.Volt,
            EnteredValue = 13.8,
            EnteredUnit = UnitConverter.Volt,
            SpecSource = "TODO: needs spec from manual",
            PassFail = PassFail.NotApplicable,
            Source = ReadingSource.Scan,
            IsSimulated = true,
        };
        await dataServices.Readings.AddAsync(simReading);
        await vm.ReloadWorkOrderReadingsAsync();

        var simRow = Assert.Single(vm.WorkOrderReadings, r => r.Id == simReading.Id);
        Assert.True(simRow.IsSimulated);
        Assert.Equal("SIMULATED", simRow.ProvenanceBadgeText);
        Assert.Equal(Visibility.Visible, simRow.SimulatedBadgeVisibility);
    }

    [Fact]
    public async Task SingleValueSpec_1500Psi_YieldsNotApplicable_InBothGuidedWalkerAndManualReadings_WhileNormalRangeYieldsPassFail()
    {
        var canon1500 = UnitConverter.ToCanonical(1500.0, UnitConverter.Psi);
        var canon2300 = UnitConverter.ToCanonical(2300.0, UnitConverter.Psi);
        var canon2500 = UnitConverter.ToCanonical(2500.0, UnitConverter.Psi);
        var canon2400 = UnitConverter.ToCanonical(2400.0, UnitConverter.Psi);
        var canon1800 = UnitConverter.ToCanonical(1800.0, UnitConverter.Psi);

        // 1. Direct helper checks (ADR-009 §5: specMin == specMax -> NotApplicable)
        Assert.Equal(PassFail.NotApplicable, TroubleshootingUnitHelper.ComputePassFail(canon1500, canon1500, canon1500));
        Assert.Equal(PassFail.NotApplicable, TroubleshootingUnitHelper.ComputePassFail(canon1800, canon1500, canon1500));
        Assert.Equal(PassFail.NotApplicable, TroubleshootingUnitHelper.ComputePassFail(canon1500, null, null));
        Assert.Equal(PassFail.Pass, TroubleshootingUnitHelper.ComputePassFail(canon2400, canon2300, canon2500));
        Assert.Equal(PassFail.Fail, TroubleshootingUnitHelper.ComputePassFail(canon1800, canon2300, canon2500));

        // 2. Guided walker + Manual readings end-to-end check on 1500 psi single-value spec
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-single-spec-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();

        GuidedNextRequest? capturedGuidedNext = null;
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.GuidedNextPath)
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                capturedGuidedNext = JsonSerializer.Deserialize<GuidedNextRequest>(body, AiServiceJson.Options);
            }

            var nextStep = new GuidedStep
            {
                SessionId = "sess-single-1",
                Available = true,
                TreeId = "tree-1",
                StepId = "step-2",
                StepOrder = 2,
                Instruction = "Done.",
            };
            return CreateJsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(nextStep, AiServiceJson.Options));
        });

        using var httpClient = new HttpClient(handler);
        using var aiClient = new AiServiceClient(httpClient, TestToken);
        await using var vm = new MainShellViewModel(
            dataServices,
            new DiagnosticAdapterFactory(),
            dispatcherQueue: null,
            aiClient: aiClient);

        await vm.ReloadDatabaseRecordsAsync();
        await vm.CreateActiveWorkOrderAsync();

        vm.CurrentGuidedStep = new GuidedStep
        {
            SessionId = "sess-single-1",
            Available = true,
            TreeId = "tree-1",
            StepId = "step-1500",
            StepOrder = 1,
            Instruction = "Check main relief setting.",
            ReadingType = ReadingType.HydraulicPressure,
            TestPointId = "tp-tug66028-main-relief",
            SpecMin = canon1500,
            SpecMax = canon1500,
            SpecSource = "TUG Model 660-28 Operation Manual CD397-Oper, page 101",
        };

        vm.GuidedReadingValue = 1500.0;
        vm.GuidedReadingUnit = UnitConverter.Psi;
        Assert.Equal(PassFail.NotApplicable, vm.GuidedReadingComputedPassFail);

        await vm.NextGuidedStepAsync(null);

        Assert.NotNull(capturedGuidedNext);
        Assert.Equal(PassFail.NotApplicable, capturedGuidedNext!.Verdict);

        var persisted = Assert.Single(await dataServices.Readings.GetAllAsync());
        Assert.Equal(PassFail.NotApplicable, persisted.PassFail);
    }

    [Fact]
    public async Task GuidedDraftTreeBadge_FlipsVisibilityWithTreeStatus()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-draft-badge-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();

        // Add an approved tree alongside the seeded draft trees
        var approvedTree = new TroubleshootingTree
        {
            Id = "tree-approved-test-01",
            EquipmentModelId = "eqm-tug-660-28",
            SymptomId = "sym-engine-no-crank",
            Title = "Approved No-Crank Tree",
            System = "Electrical",
            Status = TreeStatus.Approved,
            Source = TreeSource.Handwritten,
        };
        await dataServices.TroubleshootingTrees.AddAsync(approvedTree);

        await using var vm = new MainShellViewModel(dataServices, new DiagnosticAdapterFactory());
        await vm.ReloadDatabaseRecordsAsync();

        Assert.Equal("DRAFT — not yet approved", vm.GuidedDraftTreeBadgeText);
        Assert.False(vm.IsCurrentGuidedTreeDraft);
        Assert.Equal(Visibility.Collapsed, vm.GuidedDraftTreeBadgeVisibility);

        // 1. Seeded draft tree -> badge visible
        vm.CurrentGuidedStep = new GuidedStep
        {
            SessionId = "sess-draft-1",
            Available = true,
            TreeId = "tree-tug66028-no-crank",
            StepId = "step-1",
            StepOrder = 1,
            Instruction = "Check battery disconnect switch.",
        };
        Assert.True(vm.IsCurrentGuidedTreeDraft);
        Assert.Equal(Visibility.Visible, vm.GuidedDraftTreeBadgeVisibility);

        // 2. Approved tree -> badge collapsed
        vm.CurrentGuidedStep = new GuidedStep
        {
            SessionId = "sess-approved-1",
            Available = true,
            TreeId = "tree-approved-test-01",
            StepId = "step-1",
            StepOrder = 1,
            Instruction = "Check battery disconnect switch.",
        };
        Assert.False(vm.IsCurrentGuidedTreeDraft);
        Assert.Equal(Visibility.Collapsed, vm.GuidedDraftTreeBadgeVisibility);

        // 3. Available == false -> badge collapsed
        vm.CurrentGuidedStep = new GuidedStep
        {
            SessionId = "sess-unavail-1",
            Available = false,
            TreeId = "tree-tug66028-no-crank",
            Instruction = "No troubleshooting tree is available.",
        };
        Assert.False(vm.IsCurrentGuidedTreeDraft);
        Assert.Equal(Visibility.Collapsed, vm.GuidedDraftTreeBadgeVisibility);
    }

    [Fact]
    public async Task GuidedWalker_ExplicitPassWithoutEnteredValue_DoesNotPersistZeroReading_AndTodoReadingYieldsNaWithoutAdvancing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-explicit-pass-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();

        var canon1500 = UnitConverter.ToCanonical(1500.0, UnitConverter.Psi);
        var canon2300 = UnitConverter.ToCanonical(2300.0, UnitConverter.Psi);
        var canon2500 = UnitConverter.ToCanonical(2500.0, UnitConverter.Psi);

        var capturedRequests = new List<GuidedNextRequest>();

        var step1Ranged = new GuidedStep
        {
            SessionId = "sess-exp-1",
            Available = true,
            TreeId = "tree-tug66028-hyd-no-lift",
            StepId = "step-ranged-01",
            StepOrder = 1,
            Instruction = "Check regulated pressure.",
            ReadingType = ReadingType.HydraulicPressure,
            TestPointId = "tp-tug66028-main-relief",
            SpecMin = canon2300,
            SpecMax = canon2500,
            SpecSource = "Manual page 101",
            NextOnPass = "step-single-02",
            NextOnFail = "step-fail",
        };

        var step2SingleValue = new GuidedStep
        {
            SessionId = "sess-exp-1",
            Available = true,
            TreeId = "tree-tug66028-hyd-no-lift",
            StepId = "step-single-02",
            StepOrder = 2,
            Instruction = "Check 1500 psi relief setting.",
            ReadingType = ReadingType.HydraulicPressure,
            TestPointId = "tp-tug66028-main-relief",
            SpecMin = canon1500,
            SpecMax = canon1500,
            SpecSource = "TUG Model 660-28 Operation Manual CD397-Oper, page 101",
            NextOnPass = "step-done-03",
            NextOnFail = "step-fail",
        };

        var step3Done = new GuidedStep
        {
            SessionId = "sess-exp-1",
            Available = true,
            TreeId = "tree-tug66028-hyd-no-lift",
            StepId = "step-done-03",
            StepOrder = 3,
            Instruction = "System normal.",
            TerminalResult = "Relief pressure verified.",
        };

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.GuidedNextPath)
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                var nextReq = JsonSerializer.Deserialize<GuidedNextRequest>(body, AiServiceJson.Options)!;
                capturedRequests.Add(nextReq);

                // Mirror AI service advance() behavior:
                // - on step-ranged-01 with verdict==Pass -> advance to step-single-02
                // - on step-single-02 with verdict==NotApplicable -> stay on step-single-02 (do not advance)
                // - on step-single-02 with verdict==Pass -> advance to step-done-03
                if (nextReq.StepId == "step-ranged-01" && nextReq.Verdict == PassFail.Pass)
                {
                    return CreateJsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(step2SingleValue, AiServiceJson.Options));
                }

                if (nextReq.StepId == "step-single-02" && nextReq.Verdict == PassFail.NotApplicable)
                {
                    return CreateJsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(step2SingleValue, AiServiceJson.Options));
                }

                if (nextReq.StepId == "step-single-02" && nextReq.Verdict == PassFail.Pass)
                {
                    return CreateJsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(step3Done, AiServiceJson.Options));
                }
            }

            return CreateJsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(step1Ranged, AiServiceJson.Options));
        });

        using var httpClient = new HttpClient(handler);
        using var aiClient = new AiServiceClient(httpClient, TestToken);
        await using var vm = new MainShellViewModel(
            dataServices,
            new DiagnosticAdapterFactory(),
            dispatcherQueue: null,
            aiClient: aiClient);

        await vm.ReloadDatabaseRecordsAsync();
        await vm.CreateActiveWorkOrderAsync();

        // Case (2): Reading step with no value entered -> GuidedReadingValue is null.
        // Clicking explicit "pass" does NOT persist a 0.0 reading and sends Verdict == Pass (not Fail).
        vm.CurrentGuidedStep = step1Ranged;
        Assert.Null(vm.GuidedReadingValue);
        Assert.True(double.IsNaN(vm.GuidedReadingBoxValue));

        await vm.NextGuidedStepAsync("pass");

        var req1 = Assert.Single(capturedRequests);
        Assert.Equal(PassFail.Pass, req1.Verdict);
        Assert.Null(req1.ReadingValue);
        Assert.Null(req1.ReadingUnit);
        Assert.Empty(await dataServices.Readings.GetAllAsync());
        Assert.Equal("step-single-02", vm.CurrentGuidedStep?.StepId);

        // Case (3): Entering a value on a single-value/TODO step and clicking "auto" ("Record Reading & Next Step")
        // persists the reading with PassFail == NotApplicable, sends Verdict == NotApplicable, and stays on the step (does not advance).
        vm.GuidedReadingValue = 1498.0;
        vm.GuidedReadingUnit = UnitConverter.Psi;
        await vm.NextGuidedStepAsync("auto");

        Assert.Equal(2, capturedRequests.Count);
        var req2 = capturedRequests[1];
        Assert.Equal(PassFail.NotApplicable, req2.Verdict);
        Assert.Equal(1498.0, req2.ReadingValue);
        Assert.Equal(UnitConverter.Psi, req2.ReadingUnit);
        Assert.Equal("step-single-02", vm.CurrentGuidedStep?.StepId);

        var readingsAfterAuto = await dataServices.Readings.GetAllAsync();
        var singleSaved = Assert.Single(readingsAfterAuto);
        Assert.Equal(1498.0, singleSaved.EnteredValue);
        Assert.Equal(PassFail.NotApplicable, singleSaved.PassFail);

        // Clicking explicit "pass" afterwards on the same step with the same value does NOT duplicate the reading row
        // and sends the technician's explicit Verdict == Pass to advance to step-done-03.
        await vm.NextGuidedStepAsync("pass");

        Assert.Equal(3, capturedRequests.Count);
        var req3 = capturedRequests[2];
        Assert.Equal(PassFail.Pass, req3.Verdict);
        Assert.Single(await dataServices.Readings.GetAllAsync());
        Assert.Equal("step-done-03", vm.CurrentGuidedStep?.StepId);
    }

    [Fact]
    public async Task SearchRepairHistoryAsync_SendsTokenHeader_SerializesSnakeCase_AndDeserializesCandidatesAndFallback()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        string? capturedToken = null;

        var responseJson = """
            {
              "candidates": [
                {
                  "candidate_repair_id": "rep-uuid-01",
                  "repair_description": "Replace main hydraulic pump",
                  "system": "hydraulic",
                  "match_tier": "model",
                  "similarity_score": 0.83,
                  "success_rate": 0.786,
                  "success_count": 45,
                  "attempt_count": 50,
                  "average_labor_hours": 2.1,
                  "last_done_at": "2026-10-27T12:00:00.0000000Z",
                  "source_work_order_ids": ["wo-1001", "wo-1002"],
                  "evidence": "Fixed 45 of 50 on TUG 660-28, avg 2.1 hrs, last done 3 weeks ago, WO #004471"
                },
                {
                  "candidate_repair_id": "rep-uuid-02",
                  "repair_description": "Clean main relief valve cartridge",
                  "system": "hydraulic",
                  "match_tier": "family",
                  "similarity_score": 0.65,
                  "success_rate": 0.207,
                  "success_count": 1,
                  "attempt_count": 1,
                  "average_labor_hours": null,
                  "last_done_at": null,
                  "source_work_order_ids": ["wo-1003"],
                  "evidence": "Fixed 1 of 1 on TUG 600i, WO #004490"
                }
              ],
              "fallback": true
            }
            """;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            capturedRequest = req;
            if (req.Headers.TryGetValues(AiServiceProtocol.TokenHeader, out var tokens))
            {
                capturedToken = tokens.FirstOrDefault();
            }
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return CreateJsonResponse(HttpStatusCode.OK, responseJson);
        });

        using var httpClient = new HttpClient(handler);
        using var client = new AiServiceClient(httpClient, TestToken);

        var response = await client.SearchRepairHistoryAsync(new RepairHistorySearchRequest
        {
            WorkOrderId = "wo-trigger-01",
            QueryText = "hydraulic lift slow under load",
            TopK = 5,
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("http://127.0.0.1:8765/search/repair-history", capturedRequest.RequestUri?.ToString());
        Assert.Equal(TestToken, capturedToken);
        Assert.NotNull(capturedBody);

        using (var doc = JsonDocument.Parse(capturedBody!))
        {
            var root = doc.RootElement;
            Assert.Equal("wo-trigger-01", root.GetProperty("work_order_id").GetString());
            Assert.Equal("hydraulic lift slow under load", root.GetProperty("query_text").GetString());
            Assert.Equal(5, root.GetProperty("top_k").GetInt32());
        }

        Assert.True(response.Fallback);
        Assert.Equal(2, response.Candidates.Count);

        var first = response.Candidates[0];
        Assert.Equal("rep-uuid-01", first.CandidateRepairId);
        Assert.Equal("Replace main hydraulic pump", first.RepairDescription);
        Assert.Equal("hydraulic", first.System);
        Assert.Equal(MatchTier.Model, first.MatchTier);
        Assert.Equal(0.83, first.SimilarityScore, 3);
        Assert.Equal(0.786, first.SuccessRate, 3);
        Assert.Equal(45, first.SuccessCount);
        Assert.Equal(50, first.AttemptCount);
        Assert.Equal(2.1, first.AverageLaborHours!.Value, 3);
        Assert.Equal("2026-10-27T12:00:00.0000000Z", first.LastDoneAt);
        Assert.Equal(new[] { "wo-1001", "wo-1002" }, first.SourceWorkOrderIds);
        Assert.Contains("Fixed 45 of 50", first.Evidence);

        var second = response.Candidates[1];
        Assert.Equal("rep-uuid-02", second.CandidateRepairId);
        Assert.Equal(MatchTier.Family, second.MatchTier);
        Assert.Null(second.AverageLaborHours);
        Assert.Null(second.LastDoneAt);

        // Convenience overload check
        var convResponse = await client.SearchRepairHistoryAsync("wo-trigger-02", "no lift", 3);
        Assert.Equal(2, convResponse.Candidates.Count);
    }

    [Fact]
    public async Task Phase5_RecommendationsPanel_PreservesCandidateOrder_GroupsByMatchTier_ShowsBadges_AndWritesPresentAndAcceptRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-phase5-reco-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();

        // Seed a historical work order + repair row so candidate_repair_id FK resolves
        var histAsset = new Asset
        {
            Id = "asset-hist-01",
            AssetKind = AssetKind.Equipment,
            AssetNumber = "GSE-HIST-01",
            Name = "Historical Belt Loader",
            ModelId = "eqm-tug-660-28",
            Status = AssetStatus.InService,
        };
        await dataServices.Assets.AddAsync(histAsset);

        var histWo = new WorkOrder
        {
            Id = "wo-hist-4471",
            Number = "004471",
            AssetId = histAsset.Id,
            Status = WorkOrderStatus.Closed,
            OpenedAt = "2026-10-01T10:00:00.0000000Z",
            ClosedAt = "2026-10-01T14:00:00.0000000Z",
            ComplaintSummary = "Hydraulic boom slow",
        };
        await dataServices.WorkOrders.AddAsync(histWo);

        var histRepair1 = new Repair
        {
            Id = "rep-hist-01",
            WorkOrderId = histWo.Id,
            Description = "Replace main hydraulic pump",
            System = "Hydraulic",
            LaborHours = 2.1,
            PerformedAt = "2026-10-01T13:00:00.0000000Z",
        };
        var histRepair2 = new Repair
        {
            Id = "rep-hist-02",
            WorkOrderId = histWo.Id,
            Description = "Inspect and clean relief cartridge",
            System = "Hydraulic",
            LaborHours = 1.2,
            PerformedAt = "2026-10-01T12:00:00.0000000Z",
        };
        await dataServices.Repairs.AddAsync(histRepair1);
        await dataServices.Repairs.AddAsync(histRepair2);

        // Intentionally order candidate #0 with LOWER similarity_score than candidate #1 within the same tier
        // to verify the UI does NOT re-sort candidates (rank 0 = best as ordered by the recommender).
        var searchResponse = new RepairHistorySearchResponse
        {
            Fallback = true,
            Candidates = new[]
            {
                new RepairCandidate
                {
                    CandidateRepairId = histRepair1.Id,
                    RepairDescription = "Replace main hydraulic pump",
                    System = "Hydraulic",
                    MatchTier = MatchTier.Model,
                    SimilarityScore = 0.60,
                    SuccessRate = 0.786,
                    SuccessCount = 45,
                    AttemptCount = 50,
                    AverageLaborHours = 2.1,
                    LastDoneAt = "2026-10-27T12:00:00.0000000Z",
                    SourceWorkOrderIds = new[] { histWo.Id },
                    Evidence = "Fixed 45 of 50 on TUG 660-28, avg 2.1 hrs, last done 3 weeks ago, WO #004471",
                },
                new RepairCandidate
                {
                    CandidateRepairId = histRepair2.Id,
                    RepairDescription = "Inspect and clean relief cartridge",
                    System = "Hydraulic",
                    MatchTier = MatchTier.Model,
                    SimilarityScore = 0.95,
                    SuccessRate = 0.520,
                    SuccessCount = 8,
                    AttemptCount = 10,
                    AverageLaborHours = null,
                    LastDoneAt = null,
                    SourceWorkOrderIds = new[] { histWo.Id },
                    Evidence = "Fixed 8 of 10 on TUG 660-28, WO #004471",
                },
                new RepairCandidate
                {
                    CandidateRepairId = "rep-unseeded-id",
                    RepairDescription = "Adjust conveyor drive tension",
                    System = "Drivetrain",
                    MatchTier = MatchTier.Family,
                    SimilarityScore = 0.50,
                    SuccessRate = 0.410,
                    SuccessCount = 5,
                    AttemptCount = 7,
                    AverageLaborHours = 1.5,
                    LastDoneAt = "2026-09-15T08:00:00.0000000Z",
                    SourceWorkOrderIds = new[] { histWo.Id },
                    Evidence = "Fixed 5 of 7 on TUG 600i, avg 1.5 hrs, last done 2 months ago, WO #004471",
                },
            },
        };

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == AiServiceProtocol.SearchRepairHistoryPath)
            {
                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(searchResponse, AiServiceJson.Options)));
            }
            return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, "{}"));
        });

        using var httpClient = new HttpClient(handler);
        using var aiClient = new AiServiceClient(httpClient, TestToken);
        await using var vm = new MainShellViewModel(
            dataServices,
            new DiagnosticAdapterFactory(),
            dispatcherQueue: null,
            aiClient: aiClient);

        await vm.ReloadDatabaseRecordsAsync();
        await vm.CreateActiveWorkOrderAsync();

        // Verify WO-number assignment on creation ("004472" since "004471" was seeded)
        Assert.Equal("004472", vm.ActiveWorkOrder!.Number);
        Assert.Equal("WO #004472", vm.ActiveWorkOrderNumberDisplay);

        // Navigate to Recommendations panel -> loads candidates & writes Presented=true rows
        await vm.SelectNavRailItem("Recommendations");
        Assert.True(vm.IsRecommendationsViewVisible);
        Assert.True(vm.IsRecommendationFallback);
        Assert.Equal(Visibility.Visible, vm.RecommendationFallbackBadgeVisibility);
        Assert.Contains("text-only ranking", vm.RecommendationFallbackBadgeText);
        Assert.Contains("AI SUGGESTION", vm.RecommendationSuggestionBadgeText);

        // Verify candidates are in exact returned order (never re-sorted) and grouped by MatchTier
        Assert.Equal(3, vm.RecommendationCandidates.Count);
        Assert.Equal("Replace main hydraulic pump", vm.RecommendationCandidates[0].RepairDescription);
        Assert.Equal("78.6%", vm.RecommendationCandidates[0].SuccessRateDisplay);
        Assert.Equal(Visibility.Visible, vm.RecommendationCandidates[0].AverageLaborHoursVisibility);
        Assert.Equal(Visibility.Visible, vm.RecommendationCandidates[0].LastDoneAtVisibility);

        Assert.Equal("Inspect and clean relief cartridge", vm.RecommendationCandidates[1].RepairDescription);
        Assert.Equal(Visibility.Collapsed, vm.RecommendationCandidates[1].AverageLaborHoursVisibility);
        Assert.Equal(Visibility.Collapsed, vm.RecommendationCandidates[1].LastDoneAtVisibility);

        Assert.Equal(2, vm.RecommendationTierGroups.Count);
        Assert.Equal(MatchTier.Model, vm.RecommendationTierGroups[0].Tier);
        Assert.Equal(2, vm.RecommendationTierGroups[0].Candidates.Count);
        Assert.Equal(MatchTier.Family, vm.RecommendationTierGroups[1].Tier);
        Assert.Single(vm.RecommendationTierGroups[1].Candidates);

        // Verify 3 presented recommendation rows were written to SQLite
        var savedRecs = await dataServices.Recommendations.FindAsync("work_order_id", vm.ActiveWorkOrder.Id);
        Assert.Equal(3, savedRecs.Count);
        Assert.All(savedRecs, r =>
        {
            Assert.True(r.Presented);
            Assert.False(r.Accepted);
        });

        // Accept candidate #0 -> updates recommendations row to Accepted=true and links a Repair row to the WO
        await vm.AcceptRecommendationAsync(vm.RecommendationCandidates[0]);
        Assert.True(vm.RecommendationCandidates[0].IsAccepted);

        var recsAfterAccept = await dataServices.Recommendations.FindAsync("work_order_id", vm.ActiveWorkOrder.Id);
        var acceptedRow = Assert.Single(recsAfterAccept, r => r.Rank == 0);
        Assert.True(acceptedRow.Presented);
        Assert.True(acceptedRow.Accepted);
        Assert.Equal(histRepair1.Id, acceptedRow.CandidateRepairId);

        var woRepairs = await dataServices.Repairs.FindAsync("work_order_id", vm.ActiveWorkOrder.Id);
        var linkedRepair = Assert.Single(woRepairs);
        Assert.Equal("Replace main hydraulic pump", linkedRepair.Description);
        Assert.Equal("Hydraulic", linkedRepair.System);
        Assert.Equal(2.1, linkedRepair.LaborHours);
    }

    [Fact]
    public async Task Phase5_CloseWorkOrder_AssignsNumberIfMissing_RecordsOutcome_DetectsAndConfirmsComebacks()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-phase5-close-{Guid.NewGuid():N}.db");
        var dataServices = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await dataServices.InitializeAsync();

        await using var vm = new MainShellViewModel(dataServices, new DiagnosticAdapterFactory());
        await vm.ReloadDatabaseRecordsAsync();

        // 1. Create and close Original Work Order #000001 with a hydraulic repair and outcome = Fixed
        await vm.CreateActiveWorkOrderAsync();
        var originalWo = vm.ActiveWorkOrder!;
        Assert.Equal("000001", originalWo.Number);

        await dataServices.Repairs.AddAsync(new Repair
        {
            Id = "rep-orig-1",
            WorkOrderId = originalWo.Id,
            Description = "Replace hydraulic lift valve",
            System = "Hydraulic",
            LaborHours = 1.5,
            PerformedAt = Clock.UtcNow,
        });

        var outcome1 = await vm.CloseWorkOrderAsync(OutcomeResult.Fixed, "Lift pressure restored to 2,400 psi");
        Assert.NotNull(outcome1);
        Assert.Equal(OutcomeResult.Fixed, outcome1!.Result);
        Assert.Equal("Lift pressure restored to 2,400 psi", outcome1.Reason);
        Assert.False(outcome1.ComebackDetected);
        Assert.Empty(vm.DetectedComebacksForLastClose);

        // 2. Open a second Work Order on the SAME asset without a number initially, log same system ("Hydraulic"),
        //    and close it while confirming the comeback prompt.
        var unnumberedWo = new WorkOrder
        {
            Id = "wo-repeat-unnumbered",
            Number = null,
            AssetId = originalWo.AssetId,
            Status = WorkOrderStatus.InProgress,
            OpenedAt = Clock.UtcNow,
            ComplaintSummary = "Hydraulic boom slow again",
        };
        await dataServices.WorkOrders.AddAsync(unnumberedWo);
        vm.WorkOrders.Insert(0, unnumberedWo);
        vm.SelectedWorkOrder = unnumberedWo;

        await dataServices.Repairs.AddAsync(new Repair
        {
            Id = "rep-repeat-2",
            WorkOrderId = unnumberedWo.Id,
            Description = "Replace main hydraulic pump",
            System = "Hydraulic",
            LaborHours = 2.5,
            PerformedAt = Clock.UtcNow,
        });

        string? capturedPromptMessage = null;
        vm.ComebackPromptHandler = (match, prior) =>
        {
            capturedPromptMessage = MainShellViewModel.BuildComebackPromptMessage(match, prior);
            return Task.FromResult(true); // Technician confirms the comeback
        };

        // Also test closing without explicit outcome -> defaults to Unsure with reason when no OutcomePromptHandler is set
        var outcome2 = await vm.CloseWorkOrderAsync(explicitResult: null, explicitReason: null, workOrderId: unnumberedWo.Id);
        Assert.NotNull(outcome2);
        Assert.Equal(OutcomeResult.Unsure, outcome2!.Result);
        Assert.False(string.IsNullOrWhiteSpace(outcome2.Reason));

        // Verify unnumbered WO was assigned "000002" on close
        var updatedRepeatWo = await dataServices.WorkOrders.GetByIdAsync(unnumberedWo.Id);
        Assert.NotNull(updatedRepeatWo);
        Assert.Equal("000002", updatedRepeatWo!.Number);
        Assert.Equal(WorkOrderStatus.Closed, updatedRepeatWo.Status);

        // Verify comeback prompt was shown and confirmed, and original WO's outcome has ComebackDetected == true
        Assert.NotNull(capturedPromptMessage);
        Assert.Contains("WO #000001", capturedPromptMessage!);
        Assert.Contains("Was the earlier repair a failure?", capturedPromptMessage!);

        var detectedComeback = Assert.Single(vm.DetectedComebacksForLastClose);
        Assert.True(detectedComeback.Confirmed);
        Assert.Equal(originalWo.Id, detectedComeback.OriginalWorkOrderId);
        Assert.Equal(unnumberedWo.Id, detectedComeback.ComebackWorkOrderId);

        var originalOutcomeAfterComeback = (await dataServices.Outcomes.FindAsync("work_order_id", originalWo.Id)).Single();
        Assert.True(originalOutcomeAfterComeback.ComebackDetected);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
