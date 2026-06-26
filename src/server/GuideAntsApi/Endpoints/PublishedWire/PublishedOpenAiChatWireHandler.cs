using System.Text;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints.PublishedWire;

public static class PublishedOpenAiChatWireHandler
{
internal static void LogInboundResponsesRequest(ILogger logger, OpenAiResponsesRequest request)
{
    if (!logger.IsEnabled(LogLevel.Debug)) return;
    var rawJson = JsonSerializer.Serialize(request, WireJson.DiagnosticsOptions);
    logger.LogDebug("RAW INBOUND PAYLOAD (/responses): {RawJson}", rawJson);
}

internal static void LogInboundChatCompletionsRequest(ILogger logger, OpenAiChatCompletionsRequest request)
{
    if (!logger.IsEnabled(LogLevel.Debug)) return;
    var rawJson = JsonSerializer.Serialize(request, WireJson.DiagnosticsOptions);
    logger.LogDebug("RAW INBOUND PAYLOAD (/chat/completions): {RawJson}", rawJson);
}

public static async Task<IResult> PostChatCompletionsAsync(
    HttpContext httpContext,
    [FromRoute] Guid pubId,
    [FromBody] OpenAiChatCompletionsRequest request,
    [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
    [FromServices] IPublishedConversationService publishedConversationService,
    [FromServices] ApplicationDbContext db)
{
    var loggerFactory = httpContext.RequestServices?.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    var wireLogger = loggerFactory.CreateLogger(PublishedOpenAiWireEndpoints.WireDiagnosticsLoggerCategory);
    LogInboundChatCompletionsRequest(wireLogger, request);

    var resolution = await executionContextResolver.ResolveAsync(
        httpContext,
        pubId,
        endpointName: "chat.completions",
        ct: httpContext.RequestAborted);
    if (!resolution.Success)
    {
        return resolution.ErrorResult!;
    }

    var context = resolution.Context!;
    var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(context, WireModelAliasResolver.AliasKeys.Guide, request.Model);
    if (modelAlias.ErrorResult != null)
    {
        return modelAlias.ErrorResult;
    }

    var clientToolDefinitions = WireClientRequestParser.ParseOpenAiChatClientToolDefinitions(request.Tools);
    var inboundToolResults = WireToolResultContinuation.ParseOpenAiChatToolResults(request.Messages);

    try
    {
        WireConversationExecutor.WireConversationResult conversation;
        if (inboundToolResults.Count > 0)
        {
            var continuation = await WireToolResultContinuation.ResolvePendingToolResultConversationAsync(
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
            var clientPrompt = WireClientRequestParser.BuildOpenAiChatClientPrompt(request.Messages);
            var instructions = clientPrompt.UserPrompt;
            if (string.IsNullOrWhiteSpace(instructions))
            {
                return OpenAiWireErrorResults.Create(
                    StatusCodes.Status400BadRequest,
                    "At least one textual message is required.",
                    type: "invalid_request_error",
                    code: "invalid_messages",
                    param: "messages");
            }

            var existingConversationId = await WireConversationResolver.ResolveConversationFromTranscriptAsync(
                context,
                clientPrompt.PrefixMessages,
                db,
                httpContext.RequestAborted);

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
            return OpenAiWireErrorResults.ProviderNotReady("Provider execution failed for this request.");
        }

        var toolCalls = WireResponseSerializer.BuildOpenAiChatToolCallsForResponse(conversation.ExternalToolCalls);
        if (conversation.PendingClientTool && toolCalls.Count == 0)
        {
            return OpenAiWireErrorResults.UnsupportedFeature(
                "This request triggered client-side tool execution, but no external tool payload was produced.");
        }

        var finishReason = conversation.PendingClientTool ? "tool_calls" : "stop";
        var publicApiOrigin = WireResponseSerializer.ResolvePublicApiOrigin(httpContext);
        var assistantContent = string.IsNullOrWhiteSpace(conversation.Text)
            ? null
            : WireResponseSerializer.RewritePublishedContentUrls(conversation.Text, publicApiOrigin);

        if (request.Stream == true)
        {
            var createdStream = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var completionId = $"chatcmpl_{Guid.NewGuid():N}";
            var ssePayload = WireResponseSerializer.BuildOpenAiChatCompletionsSsePayload(
                completionId,
                modelAlias.Alias,
                createdStream,
                assistantContent,
                toolCalls,
                finishReason,
                conversation.PromptTokens,
                conversation.CompletionTokens);
            return Results.Text(
                ssePayload,
                "text/event-stream",
                Encoding.UTF8);
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usage = WireStreamPayloadReader.BuildOpenAiUsage(conversation.PromptTokens, conversation.CompletionTokens);
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = assistantContent
        };
        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls;
        }

        return Results.Json(new
        {
            id = $"chatcmpl_{Guid.NewGuid():N}",
            @object = "chat.completion",
            created,
            model = modelAlias.Alias,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message,
                    finish_reason = finishReason
                }
            },
            usage
        }, WireJson.SerializationOptions);
    }
    catch (RoutingException ex)
    {
        return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
    }
}

public static async Task<IResult> PostResponsesAsync(
    HttpContext httpContext,
    [FromRoute] Guid pubId,
    [FromBody] OpenAiResponsesRequest request,
    [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
    [FromServices] IPublishedConversationService publishedConversationService,
    [FromServices] ApplicationDbContext db)
{
    var loggerFactory = httpContext.RequestServices?.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    var wireLogger = loggerFactory.CreateLogger(PublishedOpenAiWireEndpoints.WireDiagnosticsLoggerCategory);
    LogInboundResponsesRequest(wireLogger, request);

    var resolution = await executionContextResolver.ResolveAsync(
        httpContext,
        pubId,
        endpointName: "responses",
        ct: httpContext.RequestAborted);
    if (!resolution.Success)
    {
        return resolution.ErrorResult!;
    }

    var context = resolution.Context!;
    var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(context, WireModelAliasResolver.AliasKeys.Guide, request.Model);
    if (modelAlias.ErrorResult != null)
    {
        return modelAlias.ErrorResult;
    }

    var clientToolDefinitions = WireClientRequestParser.ParseOpenAiResponsesClientToolDefinitions(request.Tools);
    var inboundToolResults = WireToolResultContinuation.ParseOpenAiResponsesToolResults(request.Input);

    try
    {
        WireConversationExecutor.WireConversationResult conversation;
        if (inboundToolResults.Count > 0)
        {
            Guid? resumeConversationId = null;
            if (!string.IsNullOrWhiteSpace(request.PreviousResponseId))
            {
                var continuation = await WireConversationResolver.ResolveResponsesConversationAsync(
                    context,
                    request.PreviousResponseId,
                    db,
                    httpContext.RequestAborted);
                if (continuation.ErrorResult != null)
                {
                    return continuation.ErrorResult;
                }

                resumeConversationId = continuation.ConversationId;
            }

            if (!resumeConversationId.HasValue)
            {
                var continuation = await WireToolResultContinuation.ResolvePendingToolResultConversationAsync(
                    context,
                    db,
                    inboundToolResults,
                    httpContext.RequestAborted);
                if (continuation.ErrorResult != null)
                {
                    return continuation.ErrorResult;
                }

                resumeConversationId = continuation.ConversationId;
            }

            await WireToolResultContinuation.AppendAnthropicToolResultsAsync(
                db,
                resumeConversationId!.Value,
                inboundToolResults,
                httpContext.RequestAborted);

            conversation = await WireConversationExecutor.ResumeConversationAfterToolResultsAsync(
                publishedConversationService,
                db,
                context,
                resumeConversationId.Value,
                clientToolDefinitions,
                httpContext.RequestAborted);
        }
        else
        {
            var clientPrompt = WireClientRequestParser.BuildOpenAiResponsesClientPrompt(request.Input);
            var instructions = clientPrompt.UserPrompt;
            if (string.IsNullOrWhiteSpace(instructions))
            {
                return OpenAiWireErrorResults.Create(
                    StatusCodes.Status400BadRequest,
                    "The input field must contain text.",
                    type: "invalid_request_error",
                    code: "invalid_input",
                    param: "input");
            }

            var conversationResolution = await WireConversationResolver.ResolveResponsesStateAsync(
                context,
                request,
                clientPrompt.PrefixMessages,
                db,
                httpContext.RequestAborted);
            if (conversationResolution.ErrorResult != null)
            {
                return conversationResolution.ErrorResult;
            }

            conversation = await WireConversationExecutor.ExecuteConversationAsync(
                publishedConversationService,
                db,
                context,
                instructions,
                httpContext.RequestAborted,
                existingConversationId: conversationResolution.ConversationId,
                clientMessages: conversationResolution.ConversationId.HasValue ? null : clientPrompt.PrefixMessages,
                clientToolDefinitions: clientToolDefinitions);
        }

        if (!string.IsNullOrWhiteSpace(conversation.ErrorPayload))
        {
            return OpenAiWireErrorResults.ProviderNotReady("Provider execution failed for this request.");
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var promptTokens = conversation.PromptTokens;
        var completionTokens = conversation.CompletionTokens;
        var responseId = conversation.ResponseId ?? $"resp_{Guid.NewGuid():N}";
        var publicApiOrigin = WireResponseSerializer.ResolvePublicApiOrigin(httpContext);
        var assistantText = WireResponseSerializer.RewritePublishedContentUrls(conversation.Text, publicApiOrigin);
        var outputItems = WireResponseSerializer.BuildOpenAiResponsesOutputItems(assistantText, conversation.ExternalToolCalls);
        if (conversation.PendingClientTool && !WireResponseSerializer.ContainsFunctionCallItem(outputItems))
        {
            return OpenAiWireErrorResults.UnsupportedFeature(
                "This request triggered client-side tool execution, but no external tool payload was produced.");
        }

        if (request.Stream == true)
        {
            var ssePayload = WireResponseSerializer.BuildOpenAiResponsesSsePayload(
                responseId,
                modelAlias.Alias,
                created,
                outputItems,
                promptTokens,
                completionTokens);
            return Results.Text(
                ssePayload,
                "text/event-stream",
                Encoding.UTF8);
        }

        return Results.Json(new
        {
            id = responseId,
            conversation = WireIdCodec.FormatConversationId(conversation.ConversationId),
            @object = "response",
            created,
            status = "completed",
            model = modelAlias.Alias,
            output = outputItems,
            usage = new
            {
                input_tokens = promptTokens,
                output_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens
            }
        }, WireJson.SerializationOptions);
    }
    catch (RoutingException ex)
    {
        return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
    }
}
}
