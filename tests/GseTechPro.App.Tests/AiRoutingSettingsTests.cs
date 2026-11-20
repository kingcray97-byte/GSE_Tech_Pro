using GseTechPro.App.Services;
using GseTechPro.App.ViewModels;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Ai;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Data;
using GseTechPro.Devices;
using Microsoft.UI.Xaml;
using Xunit;

namespace GseTechPro.App.Tests;

/// <summary>
/// Phase 10A.4 UI (ADR-013): Setup &gt; AI service settings, the administrator PIN, the per-job
/// "This tablet only" switch, the <c>routing</c> sent with each chat and the route badge on answers.
/// </summary>
public sealed class AiRoutingSettingsTests
{
    private static async Task<DataServices> CreateDataAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gse-routing-{Guid.NewGuid():N}.db");
        var data = new DataServices(new DatabaseOptions { DatabasePath = dbPath });
        await data.InitializeAsync();
        return data;
    }

    private static async Task<(MainShellViewModel Vm, FakeAiClient Ai)> CreateViewModelAsync(ChatResponse? reply = null)
    {
        var ai = new FakeAiClient(reply ?? new ChatResponse { Text = "Check the relief valve.", Route = AiRoute.Tablet, Model = "lfm3-omni" });
        var vm = new MainShellViewModel(await CreateDataAsync(), new DiagnosticAdapterFactory(), aiClient: ai);
        await vm.LoadAiRoutingSettingsAsync();
        return (vm, ai);
    }

    [Fact]
    public async Task Defaults_AreTabletOnly_CloudOff_NoPin()
    {
        var service = new AiRoutingSettingsService((await CreateDataAsync()).Settings);

        var options = await service.LoadAsync();

        Assert.Equal(AiRoutingMode.TabletOnly, options.Mode);
        Assert.False(options.CloudAllowed);
        Assert.False(options.ShareJobDetailsWithCloud);
        Assert.False(await service.HasAdminPinAsync());
    }

    [Fact]
    public async Task AdminSettings_NeedTheRightPin()
    {
        var service = new AiRoutingSettingsService((await CreateDataAsync()).Settings);

        Assert.False(await service.SaveAdminSettingsAsync("1234", cloudAllowed: true, shareJobDetailsWithCloud: true));
        Assert.True(await service.SetAdminPinAsync(null, "1234"));
        Assert.False(await service.SaveAdminSettingsAsync("9999", true, true));
        Assert.False((await service.LoadAsync()).CloudAllowed);

        Assert.True(await service.SaveAdminSettingsAsync("1234", true, true));
        var options = await service.LoadAsync();
        Assert.True(options.CloudAllowed);
        Assert.True(options.ShareJobDetailsWithCloud);
    }

    [Fact]
    public async Task ChangingThePin_NeedsTheCurrentPin()
    {
        var service = new AiRoutingSettingsService((await CreateDataAsync()).Settings);
        Assert.True(await service.SetAdminPinAsync(null, "1234"));

        Assert.False(await service.SetAdminPinAsync(null, "5678"));
        Assert.False(await service.SetAdminPinAsync("0000", "5678"));
        Assert.True(await service.SetAdminPinAsync("1234", "5678"));
        Assert.True(await service.VerifyAdminPinAsync("5678"));
        Assert.False(await service.VerifyAdminPinAsync("1234"));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    [InlineData("")]
    public void InvalidPins_AreRefused(string pin) => Assert.False(AdminPinHasher.IsValidPin(pin));

    [Fact]
    public void PinHash_IsSaltedAndNeverThePlainPin()
    {
        var a = AdminPinHasher.Hash("2468");
        var b = AdminPinHasher.Hash("2468");

        Assert.DoesNotContain("2468", a);
        Assert.NotEqual(a, b);
        Assert.True(AdminPinHasher.Verify("2468", a));
        Assert.False(AdminPinHasher.Verify("2469", a));
        Assert.False(AdminPinHasher.Verify("2468", "garbage"));
    }

    [Fact]
    public async Task SharingJobDetails_IsOff_WhenCloudIsOff()
    {
        var service = new AiRoutingSettingsService((await CreateDataAsync()).Settings);
        await service.SetAdminPinAsync(null, "1234");

        await service.SaveAdminSettingsAsync("1234", cloudAllowed: false, shareJobDetailsWithCloud: true);

        Assert.False((await service.LoadAsync()).ShareJobDetailsWithCloud);
    }

    [Fact]
    public async Task RouteMode_IsSavedWithoutPin_AndSentWithChat()
    {
        var (vm, ai) = await CreateViewModelAsync();
        await using var _ = vm;

        vm.SelectedAiRouteModeIndex = (int)AiRoutingMode.PreferShopServer;
        await WaitForAsync(async () =>
            (await new AiRoutingSettingsService(GetData(vm).Settings).LoadAsync()).Mode == AiRoutingMode.PreferShopServer);

        vm.ChatInputText = "Why is the lift slow?";
        await vm.SendChatMessageAsync();

        Assert.NotNull(ai.LastRequest?.Routing);
        Assert.Equal(AiRoutingMode.PreferShopServer, ai.LastRequest!.Routing!.Mode);
        Assert.False(ai.LastRequest.Routing.CloudAllowed);
    }

    [Fact]
    public async Task JobTabletOnly_OverridesTheShopSetting()
    {
        var (vm, ai) = await CreateViewModelAsync();
        await using var _ = vm;
        vm.SelectedAiRouteModeIndex = (int)AiRoutingMode.PreferShopServer;

        vm.IsJobTabletOnly = true;
        Assert.Equal("Answers for this job: this tablet only.", vm.ChatRouteSummaryText);

        vm.ChatInputText = "Explain the active DTCs.";
        await vm.SendChatMessageAsync();

        Assert.Equal(AiRoutingMode.TabletOnly, ai.LastRequest!.Routing!.Mode);
        Assert.False(ai.LastRequest.Routing.CloudAllowed);

        vm.IsJobTabletOnly = false;
        Assert.Equal(AiRoutingMode.PreferShopServer, vm.CurrentChatRouting.Mode);
    }

    [Fact]
    public async Task CloudAnswer_GetsTheCloudBadge()
    {
        var (vm, _) = await CreateViewModelAsync(new ChatResponse { Text = "Cloud says hi.", Route = AiRoute.Cloud, Model = "shop-cloud" });
        await using var __ = vm;

        vm.ChatInputText = "Hello";
        await vm.SendChatMessageAsync();

        var answer = vm.ChatMessages.Last(m => m.Role == AiChatRoles.Assistant);
        Assert.Equal("CLOUD", answer.RouteBadgeText);
        Assert.Equal(Visibility.Visible, answer.CloudRouteBadgeVisibility);
        Assert.Equal(Visibility.Collapsed, answer.RouteBadgeVisibility);
        Assert.Contains("shop-cloud", answer.RouteBadgeToolTip);
    }

    [Fact]
    public async Task AnswerFromOlderService_IsBadgedThisTablet()
    {
        var (vm, _) = await CreateViewModelAsync(new ChatResponse { Text = "Old service.", Route = null });
        await using var __ = vm;

        vm.ChatInputText = "Hello";
        await vm.SendChatMessageAsync();

        var answer = vm.ChatMessages.Last(m => m.Role == AiChatRoles.Assistant);
        Assert.Equal("This tablet", answer.RouteBadgeText);
        Assert.Equal(Visibility.Visible, answer.RouteBadgeVisibility);
        Assert.Equal(Visibility.Collapsed, answer.CloudRouteBadgeVisibility);
    }

    [Fact]
    public async Task UnavailableNotice_NoLongerMentionsOllama()
    {
        var ai = new FakeAiClient(new HttpRequestException("refused"));
        var vm = new MainShellViewModel(await CreateDataAsync(), new DiagnosticAdapterFactory(), aiClient: ai);
        await using var _ = vm;

        vm.ChatInputText = "Hello";
        await vm.SendChatMessageAsync();

        var notice = vm.ChatMessages.Last();
        Assert.True(notice.IsDegradedNotice);
        Assert.DoesNotContain("Ollama", notice.Content);
        Assert.Contains("llama.cpp", notice.Content);
    }

    [Theory]
    [InlineData(AiRoutingMode.TabletOnly, false, "Answers: this tablet only.")]
    [InlineData(AiRoutingMode.PreferShopServer, false, "Answers: shop server, else this tablet.")]
    [InlineData(AiRoutingMode.PreferCloud, false, "Answers: shop server, else this tablet (cloud not allowed by the administrator).")]
    public void RouteSummary_SaysWhereAnswersComeFrom(AiRoutingMode mode, bool cloudAllowed, string expected) =>
        Assert.Equal(expected, MainShellViewModel.DescribeRouting(new AiRoutingOptions { Mode = mode, CloudAllowed = cloudAllowed }));

    [Fact]
    public void RouteSummary_ForAllowedCloud_SaysWhetherJobDetailsLeave()
    {
        Assert.Contains("Job details stay on this tablet",
            MainShellViewModel.DescribeRouting(new AiRoutingOptions { Mode = AiRoutingMode.PreferCloud, CloudAllowed = true }));
        Assert.Contains("Job details may go to the cloud",
            MainShellViewModel.DescribeRouting(new AiRoutingOptions { Mode = AiRoutingMode.PreferCloud, CloudAllowed = true, ShareJobDetailsWithCloud = true }));
    }

    private static DataServices GetData(MainShellViewModel vm) =>
        (DataServices)typeof(MainShellViewModel)
            .GetField("_dataServices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm)!;

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await condition())
                return;
            await Task.Delay(20);
        }
        Assert.Fail("Condition not met in time.");
    }

    private sealed class FakeAiClient : IAiClient
    {
        private readonly ChatResponse? _reply;
        private readonly Exception? _error;

        public FakeAiClient(ChatResponse reply) => _reply = reply;
        public FakeAiClient(Exception error) => _error = error;

        public AiChatRequest? LastRequest { get; private set; }

        public Task<ChatResponse> ChatFullAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return _error is not null ? Task.FromException<ChatResponse>(_error) : Task.FromResult(_reply!);
        }

        public async Task<string> ChatAsync(AiChatRequest request, CancellationToken cancellationToken = default) =>
            (await ChatFullAsync(request, cancellationToken)).Text;

        public Task<IReadOnlyList<SearchResult>> SearchManualsAsync(string query, int topK, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        public Task<SearchManualsResponse> SearchManualsFullAsync(SearchManualsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchManualsResponse());

        public Task<AiServiceHealth> HealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<AiServiceHealth>(new HttpRequestException("not used"));

        public Task<AiServiceConfig> GetConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiServiceConfig());

        public Task<string> IngestAsync(IngestRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IngestionStatus> GetIngestStatusAsync(string jobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GuidedStep> StartGuidedAsync(GuidedStartRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GuidedStep> NextGuidedAsync(GuidedNextRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
