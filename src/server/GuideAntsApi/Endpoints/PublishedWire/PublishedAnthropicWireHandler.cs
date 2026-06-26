using System.Text;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints.PublishedWire;

public static class PublishedAnthropicWireHandler
{
internal static void LogInboundAnthropicMessagesRequest(ILogger logger, AnthropicMessagesRequest request)
{
    if (!logger.IsEnabled(LogLevel.Debug)) return;
    var rawJson = JsonSerializer.Serialize(request, WireJson.DiagnosticsOptions);
    logger.LogDebug("RAW INBOUND PAYLOAD (/messages): {RawJson}", rawJson);
}

public static async Task<IResult> PostMessagesAsync(
    HttpContext httpContext,
    [FromRoute] Guid pubId,
    [FromBody] AnthropicMessagesRequest request,
    [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
    [FromServices] IPublishedConversationService publishedConversationService,
    [FromServices] ApplicationDbContext db)
{
    var loggerFactory = httpContext.RequestServices?.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    var wireLogger = loggerFactory.CreateLogger(PublishedOpenAiWireEndpoints.WireDiagnosticsLoggerCategory);
    LogInboundAnthropicMessagesRequest(wireLogger, request);

    var resolution = await executionContextResolver.ResolveAsync(
        httpContext,
        pubId,
        endpointName: "messages",
        ct: httpContext.RequestAborted);
    if (!resolution.Success)
    {
        return resolution.ErrorResult!;
    }

    var context = resolution.Context!;
    var configuredAlias = WireModelAliasResolver.ResolveConfiguredAlias(context.WireApiConfig, WireModelAliasResolver.AliasKeys.Guide);
    if (!string.IsNullOrWhiteSpace(request.Model) &&
        !string.Equals(configuredAlias, request.Model, StringComparison.OrdinalIgnoreCase))
    {
        return WireResponseSerializer.CreateAnthropicError(
            StatusCodes.Status400BadRequest,
            errorType: "invalid_request_error",
            message: $"Model alias '{request.Model}' is not configured for this endpoint.");
    }
    var clientToolDefinitions = WireClientRequestParser.ParseAnthropicClientToolDefinitions(request.Tools);
    var inboundToolResults = WireToolResultContinuation.ParseAnthropicToolResults(request.Messages);

    try
    {
        WireConversationExecutor.WireConversationResult conversation;
        if (inboundToolResults.Count > 0)
        {
            var continuation = await WireToolResultContinuation.ResolveAnthropicToolResultConversationAsync(
                context,
                db,
                inboundToolResults,
                httpContext.RequestAborted);
            if (continuation.ErrorResult != null)
            {
                return continuation.ErrorResult;
            }

            await WireToolResultContinuation.AppendAnthropicToolResultsAsync(
                db,
                continuation.ConversationId!.Value,
                inboundToolResults,
                httpContext.RequestAborted);

            conversation = await WireConversationExecutor.ResumeConversationAfterToolResultsAsync(
                publishedConversationService,
                db,
                context,
                continuation.ConversationId.Value,
                clientToolDefinitions,
                httpContext.RequestAborted);
        }
        else
        {
            var clientPrompt = WireClientRequestParser.BuildAnthropicClientPrompt(request.System, request.Messages);
            var instructions = clientPrompt.UserPrompt;
            if (string.IsNullOrWhiteSpace(instructions))
            {
                return WireResponseSerializer.CreateAnthropicError(
                    StatusCodes.Status400BadRequest,
                    errorType: "invalid_request_error",
                    message: "At least one textual message is required in 'messages' or 'system'.");
            }

            var messageIdResolution = await WireConversationResolver.ResolveConversationFromMessageIdsAsync(
                context,
                request.Messages,
                db,
                httpContext.RequestAborted);
            if (messageIdResolution.ErrorResult != null)
            {
                return messageIdResolution.ErrorResult;
            }

            var existingConversationId = messageIdResolution.ConversationId;
            if (!existingConversationId.HasValue)
            {
                existingConversationId = await WireConversationResolver.ResolveConversationFromTranscriptAsync(
                    context,
                    clientPrompt.PrefixMessages,
                    db,
                    httpContext.RequestAborted);
            }

            conversation = await WireConversationExecutor.ExecuteConversationAsync(
                publishedConversationService,
                db,
                context,
                instructions,
                httpContext.RequestAborted,
                existingConversationId: existingConversationId,
                clientMessages: existingConversationId.HasValue ? null : clientPrompt.PrefixMessages,
                clientToolDefinitions: clientToolDefinitions);
        }

        if (!string.IsNullOrWhiteSpace(conversation.ErrorPayload))
        {
            return WireResponseSerializer.CreateAnthropicError(
                StatusCodes.Status503ServiceUnavailable,
                errorType: "api_error",
                message: "Provider execution failed for this request.");
        }

        var publicApiOrigin = WireResponseSerializer.ResolvePublicApiOrigin(httpContext);
        var assistantText = WireResponseSerializer.RewritePublishedContentUrls(conversation.Text, publicApiOrigin);
        var contentBlocks = WireResponseSerializer.BuildAnthropicContentBlocks(assistantText, conversation.ExternalToolCalls);
        if (conversation.PendingClientTool && contentBlocks.All(b => !string.Equals(b.Type, "tool_use", StringComparison.Ordinal)))
        {
            return WireResponseSerializer.CreateAnthropicError(
                StatusCodes.Status400BadRequest,
                errorType: "invalid_request_error",
                message: "This request triggered client-side tool execution, but no external tool payload was produced.");
        }

        var stopReason = conversation.PendingClientTool ? "tool_use" : "end_turn";
        var messageId = conversation.AssistantMessageId.HasValue
            ? WireIdCodec.FormatAnthropicMessageId(conversation.AssistantMessageId.Value)
            : $"msg_{Guid.NewGuid():N}";

        if (request.Stream == true)
        {
            var ssePayload = WireResponseSerializer.BuildAnthropicMessageSsePayload(
                messageId,
                configuredAlias,
                contentBlocks,
                stopReason,
                conversation.PromptTokens,
                conversation.CompletionTokens);
            return Results.Text(
                ssePayload,
                "text/event-stream",
                Encoding.UTF8);
        }

        return Results.Json(new
        {
            id = messageId,
            type = "message",
            role = "assistant",
            content = contentBlocks.Select(WireResponseSerializer.BuildAnthropicResponseContentBlock).ToArray(),
            model = configuredAlias,
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = conversation.PromptTokens,
                output_tokens = conversation.CompletionTokens
            }
        }, WireJson.SerializationOptions);
    }
    catch (RoutingException ex)
    {
        return WireResponseSerializer.CreateAnthropicError(
            StatusCodes.Status503ServiceUnavailable,
            errorType: "api_error",
            message: ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return WireResponseSerializer.CreateAnthropicError(
            StatusCodes.Status503ServiceUnavailable,
            errorType: "api_error",
            message: ex.Message);
    }
}

public static async Task<IResult> PostMessagesCountTokensAsync(
    HttpContext httpContext,
    [FromRoute] Guid pubId,
    [FromBody] JsonElement request,
    [FromServices] IPublishedApiExecutionContextResolver executionContextResolver)
{
    var resolution = await executionContextResolver.ResolveAsync(
        httpContext,
        pubId,
        endpointName: "messages",
        ct: httpContext.RequestAborted);
    if (!resolution.Success)
    {
        return resolution.ErrorResult!;
    }

    var context = resolution.Context!;
    var configuredAlias = WireModelAliasResolver.ResolveConfiguredAlias(context.WireApiConfig, WireModelAliasResolver.AliasKeys.Guide);
    var requestedModel = request.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
        ? modelElement.GetString()
        : null;
    if (!string.IsNullOrWhiteSpace(requestedModel) &&
        !string.Equals(configuredAlias, requestedModel, StringComparison.OrdinalIgnoreCase))
    {
        return WireResponseSerializer.CreateAnthropicError(
            StatusCodes.Status400BadRequest,
            errorType: "invalid_request_error",
            message: $"Model alias '{requestedModel}' is not configured for this endpoint.");
    }

    return Results.Json(new
    {
        input_tokens = WireStreamPayloadReader.EstimateAnthropicInputTokens(request)
    }, WireJson.SerializationOptions);
}
}
