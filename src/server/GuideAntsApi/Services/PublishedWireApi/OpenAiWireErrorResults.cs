using System.Text.Json;
using GuideAntsApi.Services.PublishedGuides;

namespace GuideAntsApi.Services.PublishedWireApi;

public static class OpenAiWireErrorResults
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IResult AuthenticationFailed(string message, string code = "invalid_api_key") =>
        Create(
            StatusCodes.Status401Unauthorized,
            message,
            type: "authentication_error",
            code: code);

    public static IResult EndpointDisabled(string endpoint) =>
        Create(
            StatusCodes.Status403Forbidden,
            $"The endpoint '{endpoint}' is disabled for this published guide.",
            type: "invalid_request_error",
            code: "endpoint_disabled",
            param: "endpoint");

    public static IResult MissingModelAlias(string modelAlias) =>
        Create(
            StatusCodes.Status400BadRequest,
            $"Model alias '{modelAlias}' is not configured for this endpoint.",
            type: "invalid_request_error",
            code: "model_alias_not_found",
            param: "model");

    public static IResult ProviderNotReady(string message) =>
        Create(
            StatusCodes.Status503ServiceUnavailable,
            message,
            type: "server_error",
            code: "provider_not_ready");

    public static IResult RequestTooLarge(string endpoint, long? maxBytes) =>
        Create(
            StatusCodes.Status413PayloadTooLarge,
            maxBytes.HasValue
                ? $"Request body exceeds configured limit for '{endpoint}' ({maxBytes.Value} bytes)."
                : $"Request body exceeds configured limit for '{endpoint}'.",
            type: "invalid_request_error",
            code: "request_too_large");

    public static IResult LimitExceeded(PublishedGuideCostLimitResult limits) =>
        Create(
            StatusCodes.Status429TooManyRequests,
            "Published guide usage limit exceeded.",
            type: "insufficient_quota",
            code: "insufficient_quota",
            details: new
            {
                reason = limits.Reason,
                dailyLimitUsd = limits.DailyLimitUsd,
                dailyChargeUsd = limits.DailyChargeUsd,
                dailyWindowStartUtc = limits.DailyWindowStartUtc,
                dailyWindowEndUtc = limits.DailyWindowEndUtc,
                monthlyLimitUsd = limits.BillingPeriodLimitUsd,
                monthlyChargeUsd = limits.BillingPeriodChargeUsd,
                monthlyWindowStartUtc = limits.BillingPeriodStartUtc,
                monthlyWindowEndUtc = limits.BillingPeriodEndUtc
            });

    public static IResult UnsupportedFeature(string message, string? param = null) =>
        Create(
            StatusCodes.Status400BadRequest,
            message,
            type: "invalid_request_error",
            code: "unsupported_feature",
            param: param);

    public static IResult Create(
        int statusCode,
        string message,
        string type,
        string code,
        string? param = null,
        object? details = null)
    {
        var payload = new
        {
            error = new
            {
                message,
                type,
                param,
                code,
                details
            }
        };

        return Results.Json(payload, JsonOptions, statusCode: statusCode);
    }
}

