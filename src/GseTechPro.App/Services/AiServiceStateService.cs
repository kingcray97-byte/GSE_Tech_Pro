using CommunityToolkit.Mvvm.ComponentModel;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using Microsoft.UI.Dispatching;

namespace GseTechPro.App.Services;

/// <summary>
/// High-level UI readiness states for the local AI service (ADR-008).
/// </summary>
public enum AiUiState
{
    /// <summary>Service is booting or chat model is warming up (<c>status == "starting"</c>).</summary>
    WarmingUp,

    /// <summary>Chat and manual search are both available (<c>status == "ok"</c> and <c>chat_available == true</c>).</summary>
    Ready,

    /// <summary>Chat is unavailable (e.g. Ollama unreachable), but manual search is available (<c>search_available == true</c>).</summary>
    DegradedSearch,

    /// <summary>AI service is unreachable or both chat and search are unavailable.</summary>
    Unavailable,
}

/// <summary>
/// Polls <see cref="IAiClient.HealthAsync"/> and <see cref="IAiClient.GetConfigAsync"/> on a background
/// interval and exposes observable AI service health, configured model names, and degradation state
/// to the WinUI shell without blocking the UI thread.
/// </summary>
public sealed partial class AiServiceStateService : ObservableObject, IAsyncDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan BackoffPollInterval = TimeSpan.FromSeconds(30);
    public const int BackoffThreshold = 3;

    private readonly IAiClient _client;
    private readonly AiServiceLauncher? _launcher;
    private readonly DispatcherQueue? _dispatcherQueue;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private TimeSpan _basePollInterval = DefaultPollInterval;
    private bool _configLoaded;

    public AiServiceStateService(
        IAiClient client,
        AiServiceLauncher? launcher = null,
        DispatcherQueue? dispatcherQueue = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _launcher = launcher;
        _dispatcherQueue = dispatcherQueue;
    }

    public int ConsecutiveUnreachableCount { get; private set; }

    public TimeSpan CurrentPollDelay => GetCurrentPollDelay(_basePollInterval);

    public TimeSpan GetCurrentPollDelay(TimeSpan? baseInterval = null)
    {
        var initial = baseInterval ?? DefaultPollInterval;
        if (ConsecutiveUnreachableCount >= BackoffThreshold)
        {
            var scaled = TimeSpan.FromTicks(initial.Ticks * 3);
            return scaled > BackoffPollInterval ? scaled : BackoffPollInterval;
        }
        return initial;
    }

    [ObservableProperty]
    private AiServiceHealth? _health;

    [ObservableProperty]
    private AiServiceConfig? _config;

    [ObservableProperty]
    private AiUiState _uiState = AiUiState.Unavailable;

    [ObservableProperty]
    private string _chatModel = "";

    [ObservableProperty]
    private string _embeddingModel = "";

    [ObservableProperty]
    private bool _isChatAvailable;

    [ObservableProperty]
    private bool _isSearchAvailable;

    [ObservableProperty]
    private string? _degradationReason;

    [ObservableProperty]
    private string _statusIndicatorText = "AI: Unavailable";

    [ObservableProperty]
    private string _statusBannerText = "AI unavailable — check that Ollama is running. Manual search remains available when the AI service is online.";

    [ObservableProperty]
    private string _modelSummaryText = "Models: waiting for AI service config";

    public event EventHandler? StateUpdated;

    /// <summary>
    /// Maps an <see cref="AiServiceHealth"/> snapshot (or <c>null</c> when unreachable) to the
    /// canonical <see cref="AiUiState"/> for the UI.
    /// </summary>
    public static AiUiState MapHealthToUiState(AiServiceHealth? health)
    {
        if (health is null)
        {
            return AiUiState.Unavailable;
        }

        if (health.Status == AiServiceStatus.Starting ||
            string.Equals(health.DegradationReason, AiErrorCodes.AiStarting, StringComparison.OrdinalIgnoreCase))
        {
            return AiUiState.WarmingUp;
        }

        if (health.Status == AiServiceStatus.Ok && health.ChatAvailable)
        {
            return AiUiState.Ready;
        }

        if (health.SearchAvailable)
        {
            return AiUiState.DegradedSearch;
        }

        return AiUiState.Unavailable;
    }

    /// <summary>
    /// Formats the status indicator badge label for a given <see cref="AiUiState"/>.
    /// </summary>
    public static string FormatStatusIndicator(AiUiState state) => state switch
    {
        AiUiState.Ready => "AI: Ready",
        AiUiState.WarmingUp => "AI: Warming up…",
        AiUiState.DegradedSearch => "AI: Degraded (Search Only)",
        _ => "AI: Unavailable",
    };

    /// <summary>
    /// Formats the human-readable status/degradation banner message for the Tech Chat and Guided screens.
    /// </summary>
    public static string FormatStatusBanner(AiUiState state, AiServiceHealth? health, string? chatModel)
    {
        var modelLabel = string.IsNullOrWhiteSpace(chatModel) ? "configured local model" : chatModel;

        return state switch
        {
            AiUiState.Ready =>
                $"AI Ready ({modelLabel}) — Local assistant and hybrid manual search are online.",

            AiUiState.WarmingUp =>
                $"AI is starting… Warming up {modelLabel}. Manual search is available while the model loads.",

            AiUiState.DegradedSearch => health?.DegradationReason switch
            {
                "ollama_unreachable" =>
                    "AI unavailable — check that Ollama is running. Manual search remains available (BM25 text search).",
                "chat_model_missing" =>
                    $"AI chat model ({modelLabel}) is not installed in Ollama. Manual search remains available.",
                "embedding_model_missing" =>
                    "Embedding model unavailable — manual search is running in BM25 text-only fallback mode.",
                _ =>
                    "AI unavailable — check that Ollama is running. Manual search remains available.",
            },

            _ =>
                "AI unavailable — check that the local AI service and Ollama are running.",
        };
    }

    /// <summary>
    /// Performs a single poll of <c>/health</c> and (when needed) <c>/config</c>.
    /// Safe to call directly from unit tests or UI commands.
    /// </summary>
    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_launcher is not null && !_launcher.IsServiceReachable && _client is AiServiceClient concreteClient)
            {
                var reachable = await _launcher.ProbeExistingServiceAsync(concreteClient, cancellationToken).ConfigureAwait(false);
                if (!reachable)
                {
                    _configLoaded = false;
                    ApplyUnreachableState();
                    return;
                }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var health = await _client.HealthAsync(cts.Token).ConfigureAwait(false);
            AiServiceConfig? config = Config;

            if (!_configLoaded || config is null)
            {
                try
                {
                    config = await _client.GetConfigAsync(cts.Token).ConfigureAwait(false);
                    _configLoaded = true;
                }
                catch
                {
                    // /health succeeded; keep going even if /config failed transiently
                }
            }

            ApplySnapshot(health, config);
        }
        catch (Exception ex)
        {
            _configLoaded = false;
            _launcher?.MarkUnreachable(ex.Message);
            ApplyUnreachableState();
        }
    }

    /// <summary>
    /// Handles a <c>/chat</c> failure asynchronously: on a <see cref="TaskCanceledException"/> (e.g. a slow
    /// CPU generation timeout), polls <c>/health</c> rather than immediately marking the service unreachable.
    /// </summary>
    public async Task HandleChatCallFailureAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is TaskCanceledException)
        {
            await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        NotifyChatCallFailure(exception);
    }

    /// <summary>
    /// Updates the UI state when a <c>/chat</c> call receives a <c>503</c> (<c>ai_unavailable</c> or <c>ai_starting</c>)
    /// or connection error mid-session.
    /// </summary>
    public void NotifyChatCallFailure(Exception exception)
    {
        if (exception is AiServiceException aiEx)
        {
            if (aiEx.IsAiStarting)
            {
                RunOnUiThread(() =>
                {
                    UiState = AiUiState.WarmingUp;
                    IsChatAvailable = false;
                    StatusIndicatorText = FormatStatusIndicator(AiUiState.WarmingUp);
                    StatusBannerText = !string.IsNullOrWhiteSpace(aiEx.Error.Message)
                        ? aiEx.Error.Message
                        : FormatStatusBanner(AiUiState.WarmingUp, Health, ChatModel);
                    StateUpdated?.Invoke(this, EventArgs.Empty);
                });
                return;
            }

            if (aiEx.IsAiUnavailable)
            {
                var nextState = IsSearchAvailable ? AiUiState.DegradedSearch : AiUiState.Unavailable;
                RunOnUiThread(() =>
                {
                    UiState = nextState;
                    IsChatAvailable = false;
                    DegradationReason = aiEx.Error.Detail ?? "ollama_unreachable";
                    StatusIndicatorText = FormatStatusIndicator(nextState);
                    StatusBannerText = !string.IsNullOrWhiteSpace(aiEx.Error.Message)
                        ? aiEx.Error.Message
                        : FormatStatusBanner(nextState, Health, ChatModel);
                    StateUpdated?.Invoke(this, EventArgs.Empty);
                });
                return;
            }
        }

        if (exception is TaskCanceledException)
        {
            // A /chat timeout on CPU does not necessarily mean the FastAPI service died; refresh /health.
            _ = RefreshOnceAsync();
            return;
        }

        if (exception is HttpRequestException)
        {
            _configLoaded = false;
            _launcher?.MarkUnreachable(exception.Message);
            ApplyUnreachableState();
        }
    }

    /// <summary>
    /// Starts background polling of <c>/health</c> and <c>/config</c> with automatic backoff
    /// (e.g. 10 s → 30 s) after consecutive unreachable polls.
    /// </summary>
    public void StartPolling(TimeSpan? interval = null)
    {
        StopPolling();
        _basePollInterval = interval ?? DefaultPollInterval;
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        _pollTask = Task.Run(async () =>
        {
            await RefreshOnceAsync(token).ConfigureAwait(false);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var delay = GetCurrentPollDelay(_basePollInterval);
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    await RefreshOnceAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }, token);
    }

    public void StopPolling()
    {
        if (_pollCts is not null)
        {
            try
            {
                _pollCts.Cancel();
            }
            catch
            {
            }
            _pollCts.Dispose();
            _pollCts = null;
        }
    }

    private void ApplySnapshot(AiServiceHealth health, AiServiceConfig? config)
    {
        ConsecutiveUnreachableCount = 0;

        var resolvedChatModel = !string.IsNullOrWhiteSpace(config?.ChatModel)
            ? config.ChatModel
            : (health.ChatModel ?? "");

        var resolvedEmbeddingModel = !string.IsNullOrWhiteSpace(config?.EmbeddingModel)
            ? config.EmbeddingModel
            : (health.EmbeddingModel ?? "");

        var uiState = MapHealthToUiState(health);
        var indicator = FormatStatusIndicator(uiState);
        var banner = FormatStatusBanner(uiState, health, resolvedChatModel);
        var modelSummary = BuildModelSummary(resolvedChatModel, resolvedEmbeddingModel);

        RunOnUiThread(() =>
        {
            Health = health;
            if (config is not null)
            {
                Config = config;
            }
            ChatModel = resolvedChatModel;
            EmbeddingModel = resolvedEmbeddingModel;
            IsChatAvailable = health.ChatAvailable && uiState == AiUiState.Ready;
            IsSearchAvailable = health.SearchAvailable;
            DegradationReason = health.DegradationReason;
            UiState = uiState;
            StatusIndicatorText = indicator;
            StatusBannerText = banner;
            ModelSummaryText = modelSummary;
            StateUpdated?.Invoke(this, EventArgs.Empty);
        });
    }

    private void ApplyUnreachableState()
    {
        ConsecutiveUnreachableCount++;

        RunOnUiThread(() =>
        {
            Health = null;
            IsChatAvailable = false;
            IsSearchAvailable = false;
            DegradationReason = "service_unreachable";
            UiState = AiUiState.Unavailable;
            StatusIndicatorText = FormatStatusIndicator(AiUiState.Unavailable);
            StatusBannerText = FormatStatusBanner(AiUiState.Unavailable, null, ChatModel);
            ModelSummaryText = BuildModelSummary(ChatModel, EmbeddingModel);
            StateUpdated?.Invoke(this, EventArgs.Empty);
        });
    }

    private static string BuildModelSummary(string? chatModel, string? embeddingModel)
    {
        var hasChat = !string.IsNullOrWhiteSpace(chatModel);
        var hasEmbed = !string.IsNullOrWhiteSpace(embeddingModel);

        if (hasChat && hasEmbed)
        {
            return $"Chat Model: {chatModel} · Embedding: {embeddingModel}";
        }
        if (hasChat)
        {
            return $"Chat Model: {chatModel}";
        }
        if (hasEmbed)
        {
            return $"Embedding: {embeddingModel}";
        }
        return "Models: configured by AI service (/config)";
    }

    private void RunOnUiThread(Action action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcherQueue.TryEnqueue(() => action());
    }

    public async ValueTask DisposeAsync()
    {
        StopPolling();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch
            {
            }
            _pollTask = null;
        }
    }
}
