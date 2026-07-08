using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.SandboxWireApi;

public sealed class SandboxWireExecutionContext : IWireExecutionContext
{
    public const string SourceChannelValue = "sandbox_wire_api";

    public Guid ProjectId { get; init; }

    public Guid NotebookId { get; init; }

    public Guid OwnerAssistantId { get; init; }

    public Guid TargetAssistantId { get; init; }

    public string TargetAssistantName { get; init; } = string.Empty;

    public string PublisherId { get; init; } = "sandbox-wire";

    public string? ExternalUserIdentity { get; init; }

    public Guid? InternalUserId { get; init; }

    public Guid? AttributionConversationId { get; init; }

    public string SourceChannel { get; init; } = SourceChannelValue;

    public string ExternalRequestId { get; init; } = string.Empty;

    public string EndpointName { get; init; } = string.Empty;

    public PublishedWireApiEndpointFlagsDto? EndpointFlags { get; init; }

    public Dictionary<string, string>? AliasMap { get; init; }

    public PublishedWireApiMaxRequestSizesDto? MaxRequestSizes { get; init; }

    public IReadOnlyList<string> AllowedEndpoints { get; init; } = [];
}

public sealed record SandboxWireExecutionResolution(
    bool Success,
    SandboxWireExecutionContext? Context,
    IResult? ErrorResult)
{
    public static SandboxWireExecutionResolution Pass(SandboxWireExecutionContext context) =>
        new(true, context, null);

    public static SandboxWireExecutionResolution Fail(IResult errorResult) =>
        new(false, null, errorResult);
}
