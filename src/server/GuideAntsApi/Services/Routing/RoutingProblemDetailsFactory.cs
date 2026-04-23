using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Converts a <see cref="RoutingException"/> into an RFC 7807 problem+json payload
/// with a stable <c>code</c> extension. See R-2 in
/// docs/settings-and-llama-completion-requirements.md.
/// </summary>
public static class RoutingProblemDetailsFactory
{
    public const string ProblemTypeBase = "https://guideants.app/problems/routing/";

    public static ProblemDetails Create(RoutingException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var status = MapStatus(exception.Code);
        var problem = new ProblemDetails
        {
            Type = $"{ProblemTypeBase}{exception.Code.ToLowerInvariant().Replace('_', '-')}",
            Title = MapTitle(exception.Code),
            Status = status,
            Detail = exception.Message
        };

        problem.Extensions["code"] = exception.Code;
        problem.Extensions["action"] = exception.Action;

        // R-2.2: canonical extension keys are `service`, `modeId`, `modelId`,
        // `provider`, `action`, `code`. Keep them aligned with the requirement
        // doc's wording exactly so client code can switch on them without a
        // translation table.
        if (!string.IsNullOrWhiteSpace(exception.ServiceId))
        {
            problem.Extensions["service"] = exception.ServiceId;
        }

        if (!string.IsNullOrWhiteSpace(exception.ModeId))
        {
            problem.Extensions["modeId"] = exception.ModeId;
        }

        if (!string.IsNullOrWhiteSpace(exception.ProviderSection))
        {
            problem.Extensions["provider"] = exception.ProviderSection;
        }

        if (!string.IsNullOrWhiteSpace(exception.ModelId))
        {
            problem.Extensions["modelId"] = exception.ModelId;
        }

        if (exception.Blockers.Count > 0)
        {
            problem.Extensions["blockers"] = exception.Blockers;
        }

        return problem;
    }

    // R-2.4: HTTP surface MUST use 400 (caller input), 409 (state conflict),
    // or 503 (unavailable) — never 500.
    private static int MapStatus(string code) => code switch
    {
        RoutingErrorCodes.ModeNotFound => StatusCodes.Status400BadRequest,
        RoutingErrorCodes.ProviderNotReady => StatusCodes.Status409Conflict,
        RoutingErrorCodes.ModelNotReady => StatusCodes.Status409Conflict,
        RoutingErrorCodes.RuntimeNotReady => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status503ServiceUnavailable
    };

    private static string MapTitle(string code) => code switch
    {
        RoutingErrorCodes.ModeNotFound => "Routing mode not found",
        RoutingErrorCodes.ProviderNotReady => "Routing provider not ready",
        RoutingErrorCodes.ModelNotReady => "Routed model not ready",
        RoutingErrorCodes.RuntimeNotReady => "Local runtime not ready",
        _ => "Routing error"
    };
}

/// <summary>
/// Global <see cref="IExceptionHandler"/> that catches <see cref="RoutingException"/>
/// anywhere in the pipeline and writes a uniform problem+json response.
/// </summary>
public sealed class RoutingExceptionHandler : IExceptionHandler
{
    private readonly ILogger<RoutingExceptionHandler> _logger;

    public RoutingExceptionHandler(ILogger<RoutingExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not RoutingException routing)
        {
            return false;
        }

        var problem = RoutingProblemDetailsFactory.Create(routing);
        _logger.LogWarning(
            routing,
            "Routing failure {Code} for {Path} serviceId={ServiceId} modelId={ModelId} modeId={ModeId}",
            routing.Code,
            httpContext.Request.Path,
            routing.ServiceId,
            routing.ModelId,
            routing.ModeId);

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        // Pass the content type to WriteAsJsonAsync directly — the overload that
        // doesn't accept one overwrites any Response.ContentType we set above
        // with "application/json", which breaks R-2.4's problem+json contract.
        await httpContext.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }
}
