using System.Net;
using GseTechPro.Core.Contracts;

namespace GseTechPro.App.Services;

/// <summary>
/// Exception thrown by <see cref="AiServiceClient"/> when the local AI service returns a non-2xx HTTP status.
/// Carries the HTTP <see cref="StatusCode"/> and the structured <see cref="AiError"/> unwrapped from
/// <see cref="AiErrorEnvelope"/> (ADR-008).
/// </summary>
public sealed class AiServiceException : Exception
{
    public AiServiceException(HttpStatusCode statusCode, AiError error, Exception? innerException = null)
        : base(FormatMessage(statusCode, error), innerException)
    {
        StatusCode = statusCode;
        Error = error ?? new AiError();
    }

    public HttpStatusCode StatusCode { get; }

    public AiError Error { get; }

    public string Code => Error.Code;

    public string? Detail => Error.Detail;

    public bool IsAiUnavailable =>
        StatusCode == HttpStatusCode.ServiceUnavailable &&
        string.Equals(Error.Code, AiErrorCodes.AiUnavailable, StringComparison.OrdinalIgnoreCase);

    public bool IsAiStarting =>
        StatusCode == HttpStatusCode.ServiceUnavailable &&
        string.Equals(Error.Code, AiErrorCodes.AiStarting, StringComparison.OrdinalIgnoreCase);

    public bool IsUnauthorized =>
        StatusCode == HttpStatusCode.Unauthorized ||
        string.Equals(Error.Code, AiErrorCodes.Unauthorized, StringComparison.OrdinalIgnoreCase);

    public bool IsNotFound =>
        StatusCode == HttpStatusCode.NotFound ||
        string.Equals(Error.Code, AiErrorCodes.NotFound, StringComparison.OrdinalIgnoreCase);

    private static string FormatMessage(HttpStatusCode statusCode, AiError? error)
    {
        if (error is null)
        {
            return $"AI service returned HTTP {(int)statusCode} ({statusCode}).";
        }

        if (!string.IsNullOrWhiteSpace(error.Message))
        {
            return error.Message;
        }

        return $"AI service error '{error.Code}' (HTTP {(int)statusCode}).";
    }
}
