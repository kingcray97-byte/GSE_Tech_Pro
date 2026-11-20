using System.Net;
using System.Text;
using System.Text.Json;
using GseTechPro.App.Services;
using GseTechPro.Core.Ai;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>
/// ADR-012/013 contracts: route order (same table as <c>ai-service/app/routing.py</c> <c>plan_routes</c>),
/// the routing object on the chat request, the route on the answer, the per-route health, and the
/// llama.cpp degradation reasons in the status banner.
/// </summary>
public sealed class AiRoutingTests
{
    private const string TestToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // Same rows as tests/ai-service/app/test_routing.py::test_route_plan. Keep the two tables in step.
    [Theory]
    [InlineData(AiRoutingMode.TabletOnly, true, true, true, "tablet")]
    [InlineData(AiRoutingMode.PreferShopServer, true, true, true, "shop-server,tablet")]
    [InlineData(AiRoutingMode.PreferShopServer, true, false, true, "tablet")]
    [InlineData(AiRoutingMode.PreferCloud, true, true, true, "cloud,shop-server,tablet")]
    [InlineData(AiRoutingMode.PreferCloud, false, true, true, "shop-server,tablet")]
    [InlineData(AiRoutingMode.PreferCloud, true, true, false, "shop-server,tablet")]
    [InlineData(AiRoutingMode.PreferCloud, true, false, true, "cloud,tablet")]
    [InlineData(AiRoutingMode.PreferCloud, false, false, false, "tablet")]
    public void Plan_MatchesTheServiceTable(
        AiRoutingMode mode, bool cloudAllowed, bool shopConfigured, bool cloudEnabled, string expected)
    {
        var plan = AiRouting.Plan(
            new AiRoutingOptions { Mode = mode, CloudAllowed = cloudAllowed },
            shopConfigured,
            cloudEnabled);

        Assert.Equal(expected, string.Join(",", plan.Select(WireName)));
    }

    [Fact]
    public void Plan_NullOptions_IsTabletOnly()
    {
        Assert.Equal(new[] { AiRoute.Tablet }, AiRouting.Plan(null, shopServerConfigured: true, cloudEnabledInService: true));
        Assert.Equal(AiRoutingMode.TabletOnly, AiRoutingOptions.TabletOnly.Mode);
        Assert.False(AiRoutingOptions.TabletOnly.CloudAllowed);
        Assert.False(AiRoutingOptions.TabletOnly.ShareJobDetailsWithCloud);
    }

    [Fact]
    public void BadgeText_MarksCloudClearly_AndOldServicesAsTablet()
    {
        Assert.Equal("CLOUD", AiRouting.BadgeText(AiRoute.Cloud));
        Assert.Equal("Shop server", AiRouting.BadgeText(AiRoute.ShopServer));
        Assert.Equal("This tablet", AiRouting.BadgeText(AiRoute.Tablet));
        Assert.Equal("This tablet", AiRouting.BadgeText(null)); // pre-ADR-013 services are tablet-only
    }

    [Fact]
    public async Task ChatFullAsync_SendsRouting_AndReadsRouteAndModel()
    {
        string? sentBody = null;
        var handler = new Handler(async (req, ct) =>
        {
            sentBody = await req.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.OK, """
                { "text": "Check the lift cylinder seals.", "is_suggestion": true, "citations": [],
                  "route": "cloud", "model": "provider/cloud-model" }
                """);
        });
        using var http = new HttpClient(handler);
        using var client = new AiServiceClient(http, TestToken);

        var response = await client.ChatFullAsync(new AiChatRequest
        {
            Messages = new[] { new AiChatMessage { Content = "Lift drifts down." } },
            Routing = new AiRoutingOptions { Mode = AiRoutingMode.PreferCloud, CloudAllowed = true },
        });

        Assert.Equal(AiRoute.Cloud, response.Route);
        Assert.Equal("provider/cloud-model", response.Model);
        using var doc = JsonDocument.Parse(sentBody!);
        var routing = doc.RootElement.GetProperty("routing");
        Assert.Equal("prefer-cloud", routing.GetProperty("mode").GetString());
        Assert.True(routing.GetProperty("cloud_allowed").GetBoolean());
        Assert.False(routing.GetProperty("share_job_details_with_cloud").GetBoolean());
    }

    [Fact]
    public async Task ChatFullAsync_OlderServiceWithoutRoute_LeavesRouteNull()
    {
        var handler = new Handler((_, _) => Task.FromResult(
            Json(HttpStatusCode.OK, """{ "text": "ok", "is_suggestion": true, "citations": [] }""")));
        using var http = new HttpClient(handler);
        using var client = new AiServiceClient(http, TestToken);

        var response = await client.ChatFullAsync(new AiChatRequest
        {
            Messages = new[] { new AiChatMessage { Content = "hi" } },
        });

        Assert.Null(response.Route);
        Assert.Equal("This tablet", AiRouting.BadgeText(response.Route));
    }

    [Fact]
    public async Task HealthAsync_ReadsRuntimeFieldAndRoutes()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
            {
              "status": "degraded", "model_runtime_reachable": true, "ollama_reachable": true,
              "chat_available": true, "search_available": true, "chat_model": "lfm3-omni",
              "embedding_model": "nomic-embed-text", "degradation_reason": "embedding_model_missing",
              "version": "0.4.0",
              "routes": [
                { "route": "tablet", "configured": true, "available": true, "model": "lfm3-omni", "message": "Ready" },
                { "route": "shop-server", "configured": false, "available": false, "model": null, "message": "Not set up" },
                { "route": "cloud", "configured": true, "available": false, "model": "provider/m", "message": "Offline" }
              ]
            }
            """)));
        using var http = new HttpClient(handler);
        using var client = new AiServiceClient(http, TestToken);

        var health = await client.HealthAsync();

        Assert.True(health.ModelRuntimeReachable);
        Assert.True(health.RuntimeReachable);
        Assert.Equal(3, health.Routes.Count);
        Assert.Equal(AiRoute.ShopServer, health.Routes[1].Route);
        Assert.False(health.Routes[1].Configured);
        var cloud = health.Routes.Single(r => r.Route == AiRoute.Cloud);
        Assert.True(cloud.Configured);
        Assert.False(cloud.Available);
        Assert.Equal("Offline", cloud.Message);
    }

    [Fact]
    public async Task HealthAsync_OlderServiceWithOnlyOllamaField_StillReportsRuntimeReachable()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
            { "status": "ok", "ollama_reachable": true, "chat_available": true, "search_available": true,
              "chat_model": "lfm3-omni", "embedding_model": "nomic-embed-text", "degradation_reason": null,
              "version": "0.3.0" }
            """)));
        using var http = new HttpClient(handler);
        using var client = new AiServiceClient(http, TestToken);

        var health = await client.HealthAsync();

        Assert.False(health.ModelRuntimeReachable);
        Assert.True(health.RuntimeReachable);
        Assert.Empty(health.Routes);
    }

    [Fact]
    public void ModelLoading_MapsToWarmingUp()
    {
        var health = new AiServiceHealth
        {
            Status = AiServiceStatus.Starting,
            ModelRuntimeReachable = true,
            SearchAvailable = true,
            DegradationReason = "model_loading",
        };
        Assert.Equal(AiUiState.WarmingUp, AiServiceStateService.MapHealthToUiState(health));

        // Even if a service reported it with status "degraded", model_loading is still a warm-up.
        Assert.Equal(AiUiState.WarmingUp,
            AiServiceStateService.MapHealthToUiState(health with { Status = AiServiceStatus.Degraded }));
    }

    [Theory]
    [InlineData("model_runtime_unreachable")]
    [InlineData("ollama_unreachable")]
    public void RuntimeUnreachable_Banner_NamesLlamaCpp_NotOllama(string reason)
    {
        var health = new AiServiceHealth
        {
            Status = AiServiceStatus.Degraded,
            SearchAvailable = true,
            DegradationReason = reason,
        };
        var banner = AiServiceStateService.FormatStatusBanner(AiUiState.DegradedSearch, health, "lfm3-omni");

        Assert.Contains("llama.cpp", banner);
        Assert.DoesNotContain("Ollama", banner);
        Assert.Contains("Manual search remains available", banner);
    }

    [Fact]
    public void ChatModelMissing_Banner_SaysNotInstalledOnThisTablet()
    {
        var health = new AiServiceHealth { Status = AiServiceStatus.Degraded, SearchAvailable = true, DegradationReason = "chat_model_missing" };
        var banner = AiServiceStateService.FormatStatusBanner(AiUiState.DegradedSearch, health, "lfm3-omni");
        Assert.Contains("not installed on this tablet", banner);
        Assert.DoesNotContain("Ollama", banner);
    }

    private static string WireName(AiRoute route) => route switch
    {
        AiRoute.Tablet => "tablet",
        AiRoute.ShopServer => "shop-server",
        AiRoute.Cloud => "cloud",
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _send(request, cancellationToken);
    }
}
