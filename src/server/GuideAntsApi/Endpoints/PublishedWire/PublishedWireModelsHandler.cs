using GuideAntsApi.Services.PublishedWireApi;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints.PublishedWire;

public static class PublishedWireModelsHandler
{
public static async Task<IResult> GetModelsAsync(
    HttpContext httpContext,
    [FromRoute] Guid pubId,
    [FromServices] IPublishedApiExecutionContextResolver executionContextResolver)
{
    var resolution = await executionContextResolver.ResolveAsync(
        httpContext,
        pubId,
        endpointName: "models",
        ct: httpContext.RequestAborted);
    if (!resolution.Success)
    {
        return resolution.ErrorResult!;
    }

    var context = resolution.Context!;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var modelIds = WireModelAliasResolver.BuildEnabledModelAliases(context.WireApiConfig);
    var data = modelIds.Select(modelId => new
    {
        id = modelId,
        @object = "model",
        created = now,
        owned_by = "guideants",
        permission = Array.Empty<object>()
    });

    return Results.Json(new
    {
        @object = "list",
        data
    }, WireJson.SerializationOptions);
}
}
