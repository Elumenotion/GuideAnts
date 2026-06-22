using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.PublishedWireApi;

public enum PublishedApiAuthMode
{
    Anonymous,
    ApiKey,
    Webhook,
    AppIdentity
}

public sealed record PublishedApiExecutionContext(
    Guid PubId,
    Guid ProjectId,
    Guid NotebookId,
    Guid GuideId,
    PublishedGuide PublishedGuide,
    PublishedWireApiConfigDto WireApiConfig,
    PublishedApiAuthMode AuthMode,
    string? ExternalUserIdentity,
    Guid? InternalUserId,
    string SourceChannel,
    string ExternalRequestId,
    string EndpointName);

public sealed record PublishedApiExecutionResolution(
    bool Success,
    PublishedApiExecutionContext? Context,
    IResult? ErrorResult)
{
    public static PublishedApiExecutionResolution Pass(PublishedApiExecutionContext context) =>
        new(true, context, null);

    public static PublishedApiExecutionResolution Fail(IResult errorResult) =>
        new(false, null, errorResult);
}

