using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GuideAntsApi.Services.Mcp;

public static class McpPublishedGuideToolHandlers
{
    private const string McpClientToolsNote =
        " MCP invocations do not support client-side tool execution.";

    private static readonly JsonElement AssistantInputSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "instructions": {
              "type": "string",
              "description": "What you want this assistant to do."
            },
            "conversationId": {
              "type": "string",
              "description": "Optional. Continue an existing conversation thread. Omit to start a new conversation."
            },
            "title": {
              "type": "string",
              "description": "Optional title when starting a new conversation (ignored when conversationId is set)."
            }
          },
          "required": ["instructions"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ConversationGetInputSchema = ParseSchema("""
        {
          "type": "object",
          "properties": {
            "conversationId": {
              "type": "string",
              "description": "The conversation ID to retrieve."
            }
          },
          "required": ["conversationId"],
          "additionalProperties": false
        }
        """);

    public static ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
    {
        var mcpContext = request.Services?.GetService(typeof(McpPublishedGuideContext)) as McpPublishedGuideContext;
        if (mcpContext is not { IsValid: true })
        {
            return ValueTask.FromResult(new ListToolsResult { Tools = [] });
        }

        var imageEmbeddingNote = BuildImageEmbeddingNote(
            request.Services?.GetService<IOptions<McpImageEmbeddingOptions>>()?.Value);

        var tools = new List<Tool>();

        foreach (var assistant in mcpContext.AddressableAssistants)
        {
            tools.Add(new Tool
            {
                Name = assistant.ToolName,
                Title = assistant.Name,
                Description = assistant.Description + McpClientToolsNote + imageEmbeddingNote,
                InputSchema = AssistantInputSchema
            });
        }

        tools.Add(new Tool
        {
            Name = McpPublishedAssistantCatalog.ConversationGetToolName,
            Title = "Get conversation",
            Description =
                "Retrieve conversation history for a thread shared across assistants." + McpClientToolsNote,
            InputSchema = ConversationGetInputSchema
        });

        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    public static async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        var mcpContext = request.Services?.GetService(typeof(McpPublishedGuideContext)) as McpPublishedGuideContext;
        if (mcpContext is not { IsValid: true })
        {
            return ErrorResult(McpPublishedGuideInvokeService.JsonError("unauthorized", "MCP context is not valid."));
        }

        var toolName = request.Params?.Name;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return ErrorResult(McpPublishedGuideInvokeService.JsonError("missing_tool_name", "Tool name is required."));
        }

        var invokeService = request.Services!.GetRequiredService<McpPublishedGuideInvokeService>();
        var args = request.Params?.Arguments;

        if (string.Equals(toolName, McpPublishedAssistantCatalog.ConversationGetToolName, StringComparison.Ordinal))
        {
            if (!TryGetString(args, "conversationId", out var conversationId))
            {
                return ErrorResult(McpPublishedGuideInvokeService.JsonError(
                    "missing_conversation_id",
                    "The conversationId parameter is required."));
            }

            var conversationJson = await invokeService.GetConversationAsync(conversationId!, mcpContext, cancellationToken);
            if (JsonHasError(conversationJson))
                return ErrorResult(conversationJson);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = conversationJson }]
            };
        }

        var assistant = McpPublishedAssistantCatalog.FindByToolName(mcpContext.AddressableAssistants, toolName);
        if (assistant == null)
        {
            return ErrorResult(McpPublishedGuideInvokeService.JsonError(
                "unknown_tool",
                $"Tool '{toolName}' is not available for this published guide."));
        }

        if (!TryGetString(args, "instructions", out var instructions))
        {
            return ErrorResult(McpPublishedGuideInvokeService.JsonError(
                "missing_instructions",
                "The instructions parameter is required."));
        }

        TryGetOptionalString(args, "conversationId", out var convoArg);
        TryGetOptionalString(args, "title", out var title);

        var invokeResult = await invokeService.InvokeAssistantAsync(
            assistant,
            instructions!,
            convoArg,
            title,
            mcpContext,
            cancellationToken);

        if (JsonHasError(invokeResult.Json))
            return ErrorResult(invokeResult.Json);

        var content = new List<ContentBlock> { new TextContentBlock { Text = invokeResult.Json } };

        if (invokeResult.ConversationId is Guid convId && invokeResult.TurnIndex is int turnIdx)
        {
            var embedder = request.Services!.GetRequiredService<McpPublishedRunImageEmbedder>();
            var images = await embedder.EmbedTurnImagesAsync(
                mcpContext.NotebookId,
                convId,
                turnIdx,
                cancellationToken);
            content.AddRange(images);
        }

        return new CallToolResult { Content = content };
    }

    private static string BuildImageEmbeddingNote(McpImageEmbeddingOptions? options)
    {
        if (options is null || !options.EmbedImages || options.MaxImagesPerResponse <= 0)
        {
            return string.Empty;
        }

        var sources = options.IncludeModifiedFiles ? "created or modified" : "created";
        var maxMegabytes = options.MaxImageBytes / (1024.0 * 1024.0);
        var sizeText = maxMegabytes >= 1
            ? $"{maxMegabytes:0.#} MB"
            : $"{options.MaxImageBytes / 1024.0:0.#} KB";

        return $" Images {sources} during the run are returned inline as image content" +
               $" (up to {options.MaxImagesPerResponse} image{(options.MaxImagesPerResponse == 1 ? string.Empty : "s")}," +
               $" max {sizeText} each); any beyond those limits remain accessible via the URLs in the JSON response.";
    }

    private static CallToolResult ErrorResult(string json) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = json }]
        };

    private static bool JsonHasError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetString(IDictionary<string, JsonElement>? args, string key, out string? value)
    {
        value = null;
        if (args == null || !args.TryGetValue(key, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = element.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetOptionalString(IDictionary<string, JsonElement>? args, string key, out string? value)
    {
        value = null;
        if (args == null || !args.TryGetValue(key, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.Null)
            return false;

        value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
