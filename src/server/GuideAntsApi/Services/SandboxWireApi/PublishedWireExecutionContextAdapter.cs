using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.PublishedWireApi;

namespace GuideAntsApi.Services.SandboxWireApi;

public sealed class PublishedWireExecutionContextAdapter : IWireExecutionContext
{
    public PublishedWireExecutionContextAdapter(PublishedApiExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    public PublishedApiExecutionContext Context { get; }

    public Guid ProjectId => Context.ProjectId;

    public Guid NotebookId => Context.NotebookId;

    public Guid OwnerAssistantId => Context.GuideId;

    public Guid TargetAssistantId => Context.GuideId;

    public string TargetAssistantName =>
        Context.PublishedGuide.FriendlyName ?? Context.PublishedGuide.Guide?.Name ?? "guide";

    public string PublisherId => Context.PubId.ToString("D");

    public string? ExternalUserIdentity => Context.ExternalUserIdentity;

    public Guid? InternalUserId => Context.InternalUserId;

    public Guid? AttributionConversationId => null;

    public string SourceChannel => Context.SourceChannel;

    public string ExternalRequestId => Context.ExternalRequestId;

    public string EndpointName => Context.EndpointName;

    public PublishedWireApiEndpointFlagsDto? EndpointFlags => Context.WireApiConfig.EndpointFlags;

    public Dictionary<string, string>? AliasMap => Context.WireApiConfig.AliasMap;

    public PublishedWireApiMaxRequestSizesDto? MaxRequestSizes => Context.WireApiConfig.MaxRequestSizes;
}
