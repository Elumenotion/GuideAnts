using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.SandboxWireApi;

public interface IWireExecutionContext
{
    Guid ProjectId { get; }

    Guid NotebookId { get; }

    Guid OwnerAssistantId { get; }

    Guid TargetAssistantId { get; }

    string TargetAssistantName { get; }

    string PublisherId { get; }

    string? ExternalUserIdentity { get; }

    Guid? InternalUserId { get; }

    Guid? AttributionConversationId { get; }

    string SourceChannel { get; }

    string ExternalRequestId { get; }

    string EndpointName { get; }

    PublishedWireApiEndpointFlagsDto? EndpointFlags { get; }

    Dictionary<string, string>? AliasMap { get; }

    PublishedWireApiMaxRequestSizesDto? MaxRequestSizes { get; }
}

public static class WireExecutionContextHttpContextExtensions
{
    public const string HttpContextItemKey = "WireExecutionContext";

    public static IWireExecutionContext? GetWireExecutionContext(this HttpContext httpContext) =>
        httpContext.Items.TryGetValue(HttpContextItemKey, out var value) ? value as IWireExecutionContext : null;
}
