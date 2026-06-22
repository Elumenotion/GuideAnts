using System.Text.Json;

namespace GuideAntsApi.Models.Guides;

public record McpToolSourceConnectionDto(
    string Transport,
    string? Url,
    string? BridgeId,
    Dictionary<string, string>? Headers,
    string? ToolNamePrefix
);

public record McpExistingToolStateDto(
    string BackingToolId,
    string? SchemaHash,
    bool Enabled,
    string? OperationId
);

public record McpBridgeToolInputDto(
    string Name,
    string? Title,
    string? Description,
    JsonElement? InputSchema
);

public record McpTestConnectionRequest(McpToolSourceConnectionDto Connection);

public record McpTestConnectionResponse(
    bool Connected,
    string Message,
    string? ServerName,
    string? ServerVersion
);

public record McpDiscoverToolsRequest(
    McpToolSourceConnectionDto Connection,
    List<McpExistingToolStateDto>? ExistingTools,
    List<McpBridgeToolInputDto>? BridgeTools
);

public record McpDiscoveredToolDto(
    string BackingToolId,
    string Name,
    string? Title,
    string? Description,
    string SchemaHash,
    bool Selected,
    string DiffState,
    string OperationId,
    string Path,
    string Method,
    string SchemaFragmentJson
);

public record McpDiscoverToolsResponse(
    bool Success,
    string Message,
    List<McpDiscoveredToolDto> Tools,
    McpDiscoverDiffSummaryDto Diff,
    string? OpenApiSpecPatchJson
);

public record McpDiscoverDiffSummaryDto(
    int Added,
    int Changed,
    int Removed,
    int Disabled
);
