using System.Net;
using System.Text;
using System.Text.Json;
using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Contracts;

namespace GseTechPro.App.Services;

/// <summary>
/// HTTP implementation of <see cref="IAiClient"/> targeting the local FastAPI AI service on
/// <c>127.0.0.1</c> (default port <c>8765</c>) with the <c>X-GSE-Token</c> header and
/// <c>snake_case</c> JSON per ADR-008.
/// </summary>
public sealed class AiServiceClient : IAiClient, IDisposable
{
    /// <summary>
    /// Default HTTP timeout (130 s) chosen to exceed Python's 120 s <c>CHAT_TIMEOUT_S</c> so cold
    /// CPU model loads are not prematurely aborted at 30 s.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(130);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private string _baseUrl;
    private string _token;

    public AiServiceClient(
        AiServiceConfig? config = null,
        string? token = null,
        HttpClient? httpClient = null)
    {
        var effectiveBaseUrl = string.IsNullOrWhiteSpace(config?.BaseUrl)
            ? AiServiceProtocol.DefaultBaseUrl
            : config.BaseUrl;

        _baseUrl = NormalizeBaseUrl(effectiveBaseUrl);
        _token = string.IsNullOrWhiteSpace(token)
            ? AiServiceLauncher.GetDefaultFallbackOrRandomToken()
            : token.Trim();

        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = DefaultTimeout,
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    public AiServiceClient(HttpClient httpClient, string token, string? baseUrl = null)
        : this(
            new AiServiceConfig
            {
                BaseUrl = string.IsNullOrWhiteSpace(baseUrl)
                    ? (httpClient?.BaseAddress?.ToString() ?? AiServiceProtocol.DefaultBaseUrl)
                    : baseUrl,
            },
            token,
            httpClient)
    {
    }

    /// <summary>Effective base URL (e.g. <c>http://127.0.0.1:8765</c>).</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>Per-install authentication token sent in <see cref="AiServiceProtocol.TokenHeader"/>.</summary>
    public string Token => _token;

    /// <summary>Updates the target base URL and/or token at runtime.</summary>
    public void Configure(string? baseUrl = null, string? token = null)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _baseUrl = NormalizeBaseUrl(baseUrl);
        }
        if (!string.IsNullOrWhiteSpace(token))
        {
            _token = token.Trim();
        }
    }

    /// <summary>Updates <see cref="BaseUrl"/> from an effective <see cref="AiServiceConfig"/>.</summary>
    public void ApplyConfig(AiServiceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            _baseUrl = NormalizeBaseUrl(config.BaseUrl);
        }
    }

    /// <inheritdoc />
    public async Task<AiServiceHealth> HealthAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<AiServiceHealth>(
            HttpMethod.Get,
            AiServiceProtocol.HealthPath,
            requestBody: null,
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<AiServiceConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var config = await SendAsync<AiServiceConfig>(
            HttpMethod.Get,
            AiServiceProtocol.ConfigPath,
            requestBody: null,
            cancellationToken).ConfigureAwait(false);

        ApplyConfig(config);
        return config;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> ChatFullAsync(
        AiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync<ChatResponse>(
            HttpMethod.Post,
            AiServiceProtocol.ChatPath,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> ChatAsync(
        AiChatRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await ChatFullAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Text;
    }

    /// <inheritdoc />
    public async Task<SearchManualsResponse> SearchManualsFullAsync(
        SearchManualsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync<SearchManualsResponse>(
            HttpMethod.Post,
            AiServiceProtocol.SearchManualsPath,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchManualsAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var response = await SearchManualsFullAsync(
            new SearchManualsRequest
            {
                Query = query ?? "",
                TopK = topK,
            },
            cancellationToken).ConfigureAwait(false);

        return response.Results;
    }

    /// <inheritdoc />
    public async Task<RepairHistorySearchResponse> SearchRepairHistoryAsync(
        RepairHistorySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync<RepairHistorySearchResponse>(
            HttpMethod.Post,
            AiServiceProtocol.SearchRepairHistoryPath,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Convenience overload for <see cref="SearchRepairHistoryAsync(RepairHistorySearchRequest, CancellationToken)"/>.
    /// </summary>
    public async Task<RepairHistorySearchResponse> SearchRepairHistoryAsync(
        string workOrderId,
        string? queryText = null,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        return await SearchRepairHistoryAsync(
            new RepairHistorySearchRequest
            {
                WorkOrderId = workOrderId ?? "",
                QueryText = queryText,
                TopK = topK,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> IngestAsync(
        IngestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendAsync<IngestResponse>(
            HttpMethod.Post,
            AiServiceProtocol.IngestPath,
            request,
            cancellationToken).ConfigureAwait(false);

        return response.JobId;
    }

    /// <inheritdoc />
    public async Task<IngestionStatus> GetIngestStatusAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var pathAndQuery = $"{AiServiceProtocol.IngestStatusPath}?job_id={Uri.EscapeDataString(jobId)}";
        return await SendAsync<IngestionStatus>(
            HttpMethod.Get,
            pathAndQuery,
            requestBody: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GuidedStep> StartGuidedAsync(
        GuidedStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync<GuidedStep>(
            HttpMethod.Post,
            AiServiceProtocol.GuidedStartPath,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GuidedStep> NextGuidedAsync(
        GuidedNextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync<GuidedStep>(
            HttpMethod.Post,
            AiServiceProtocol.GuidedNextPath,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PhotoInspectionResponse> InspectPhotoAsync(
        PhotoInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await SendAsync<PhotoInspectionResponse>(
            HttpMethod.Post,
            AiServiceProtocol.VisionInspectPath,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string relativePathAndQuery,
        object? requestBody,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildUri(relativePathAndQuery);
        using var requestMessage = new HttpRequestMessage(method, requestUri);
        requestMessage.Headers.TryAddWithoutValidation(AiServiceProtocol.TokenHeader, _token);

        if (requestBody is not null)
        {
            var json = JsonSerializer.Serialize(requestBody, requestBody.GetType(), AiServiceJson.Options);
            requestMessage.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateServiceException(response.StatusCode, response.ReasonPhrase, responseBody);
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            throw new JsonException($"AI service returned an empty 2xx body for '{relativePathAndQuery}'.");
        }

        var parsed = JsonSerializer.Deserialize<TResponse>(responseBody, AiServiceJson.Options);
        if (parsed is null)
        {
            throw new JsonException($"AI service returned null payload for '{relativePathAndQuery}'.");
        }

        return parsed;
    }

    private Uri BuildUri(string relativePathAndQuery)
    {
        if (relativePathAndQuery.StartsWith('/'))
        {
            return new Uri(_baseUrl + relativePathAndQuery, UriKind.Absolute);
        }
        return new Uri(_baseUrl + "/" + relativePathAndQuery, UriKind.Absolute);
    }

    /// <summary>
    /// Validates and normalizes an AI service base URL, enforcing a loopback host (<see cref="Uri.IsLoopback"/>)
    /// per ADR-008.
    /// </summary>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Invalid AI service base URL '{baseUrl}'.", nameof(baseUrl));
        }

        if (!uri.IsLoopback)
        {
            throw new ArgumentException(
                $"AI service base URL host '{uri.Host}' must be a loopback address (ADR-008).",
                nameof(baseUrl));
        }

        return trimmed;
    }

    private static AiServiceException CreateServiceException(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string? responseBody)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<AiErrorEnvelope>(responseBody, AiServiceJson.Options);
                if (envelope?.Error is not null &&
                    (!string.IsNullOrWhiteSpace(envelope.Error.Code) || !string.IsNullOrWhiteSpace(envelope.Error.Message)))
                {
                    var code = string.IsNullOrWhiteSpace(envelope.Error.Code)
                        ? MapDefaultErrorCode(statusCode)
                        : envelope.Error.Code;
                    var message = string.IsNullOrWhiteSpace(envelope.Error.Message)
                        ? $"AI service error ({code})."
                        : envelope.Error.Message;

                    return new AiServiceException(
                        statusCode,
                        new AiError
                        {
                            Code = code,
                            Message = message,
                            Detail = envelope.Error.Detail,
                        });
                }
            }
            catch (JsonException)
            {
                // Fall back to status-mapped AiError below if non-JSON body was returned
            }
        }

        var fallbackCode = MapDefaultErrorCode(statusCode);
        var fallbackMessage = !string.IsNullOrWhiteSpace(responseBody)
            ? responseBody.Trim()
            : (!string.IsNullOrWhiteSpace(reasonPhrase)
                ? reasonPhrase!
                : $"AI service request failed with HTTP {(int)statusCode}.");

        return new AiServiceException(
            statusCode,
            new AiError
            {
                Code = fallbackCode,
                Message = fallbackMessage,
                Detail = null,
            });
    }

    public static string MapDefaultErrorCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => AiErrorCodes.InvalidRequest,
        HttpStatusCode.Unauthorized => AiErrorCodes.Unauthorized,
        HttpStatusCode.NotFound => AiErrorCodes.NotFound,
        HttpStatusCode.Conflict => AiErrorCodes.Conflict,
        HttpStatusCode.UnprocessableEntity => AiErrorCodes.ValidationError,
        HttpStatusCode.ServiceUnavailable => AiErrorCodes.AiUnavailable,
        _ => AiErrorCodes.Internal,
    };

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
