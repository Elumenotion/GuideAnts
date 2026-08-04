using System.Text;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Components;
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
    [FromServices] INotebookFileService notebookFileService,
    [FromServices] IHttpClientFactory httpClientFactory,
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
        WireConversationExecutor.WireConversationStreamHandle streamHandle;
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

            streamHandle = WireConversationExecutor.StartResumeConversationStream(
                publishedConversationService,
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

            var attachments = await WireImageAttachmentMaterializer.MaterializeAsync(
                notebookFileService,
                context.ProjectId,
                context.NotebookId,
                clientPrompt.UserImageUrls,
                httpClientFactory.CreateClient(),
                httpContext.RequestAborted);

            streamHandle = await WireConversationExecutor.StartConversationStreamAsync(
                publishedConversationService,
                context,
                instructions,
                httpContext.RequestAborted,
                existingConversationId: existingConversationId,
                clientMessages: existingConversationId.HasValue ? null : clientPrompt.PrefixMessages,
                clientToolDefinitions: clientToolDefinitions,
                attachments: attachments.Count == 0 ? null : attachments);
        }

        var publicApiOrigin = WireResponseSerializer.ResolvePublicApiOrigin(httpContext);

        if (request.Stream == true)
        {
            var createdStream = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var completionId = $"chatcmpl_{Guid.NewGuid():N}";
            var streamState = new WireStreamAdapter.OpenAiChatStreamState();
            var conversationId = streamHandle.ConversationId;
            var createdConversation = streamHandle.CreatedConversation;

            return Results.Stream(async outputStream =>
            {
                await foreach (var sseChunk in WireStreamAdapter.WriteOpenAiChatCompletionsSseAsync(
                                   streamHandle.Events,
                                   completionId,
                                   modelAlias.Alias,
                                   createdStream,
                                   publicApiOrigin,
                                   streamState,
                                   httpContext.RequestAborted))
                {
                    var bytes = Encoding.UTF8.GetBytes(sseChunk);
                    await outputStream.WriteAsync(bytes, httpContext.RequestAborted);
                    await outputStream.FlushAsync(httpContext.RequestAborted);
                }

                if (createdConversation)
                {
                    await WireConversationExecutor.TryGenerateWireConversationTitleAsync(db, conversationId, httpContext.RequestAborted);
                }
            }, contentType: "text/event-stream");
        }

        var conversation = await WireConversationExecutor.CollectWireConversationResultAsync(
            streamHandle.Events,
            db,
            streamHandle.ConversationId,
            httpContext.RequestAborted);

        if (streamHandle.CreatedConversation)
        {
            await WireConversationExecutor.TryGenerateWireConversationTitleAsync(
                db,
                streamHandle.ConversationId,
                httpContext.RequestAborted);
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
        var assistantContent = string.IsNullOrWhiteSpace(conversation.Text)
            ? null
            : WireResponseSerializer.RewritePublishedContentUrls(conversation.Text, publicApiOrigin);

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
    [FromServices] INotebookFileService notebookFileService,
    [FromServices] IHttpClientFactory httpClientFactory,
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
        WireConversationExecutor.WireConversationStreamHandle streamHandle;
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

            streamHandle = WireConversationExecutor.StartResumeConversationStream(
                publishedConversationService,
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

            var attachments = await WireImageAttachmentMaterializer.MaterializeAsync(
                notebookFileService,
                context.ProjectId,
                context.NotebookId,
                clientPrompt.UserImageUrls,
                httpClientFactory.CreateClient(),
                httpContext.RequestAborted);

            streamHandle = await WireConversationExecutor.StartConversationStreamAsync(
                publishedConversationService,
                context,
                instructions,
                httpContext.RequestAborted,
                existingConversationId: conversationResolution.ConversationId,
                clientMessages: conversationResolution.ConversationId.HasValue ? null : clientPrompt.PrefixMessages,
                clientToolDefinitions: clientToolDefinitions,
                attachments: attachments.Count == 0 ? null : attachments);
        }

        var publicApiOrigin = WireResponseSerializer.ResolvePublicApiOrigin(httpContext);

        if (request.Stream == true)
        {
            var streamCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var streamResponseId = $"resp_{Guid.NewGuid():N}";
            var streamState = new WireStreamAdapter.OpenAiResponsesStreamState();
            var conversationId = streamHandle.ConversationId;
            var createdConversation = streamHandle.CreatedConversation;
            var conversationWireId = WireIdCodec.FormatConversationId(streamHandle.ConversationId);

            return Results.Stream(async outputStream =>
            {
                await foreach (var sseChunk in WireStreamAdapter.WriteOpenAiResponsesSseAsync(
                                   streamHandle.Events,
                                   streamResponseId,
                                   modelAlias.Alias,
                                   streamCreated,
                                   conversationWireId,
                                   publicApiOrigin,
                                   streamState,
                                   httpContext.RequestAborted))
                {
                    var bytes = Encoding.UTF8.GetBytes(sseChunk);
                    await outputStream.WriteAsync(bytes, httpContext.RequestAborted);
                    await outputStream.FlushAsync(httpContext.RequestAborted);
                }

                if (createdConversation)
                {
                    await WireConversationExecutor.TryGenerateWireConversationTitleAsync(
                        db,
                        conversationId,
                        httpContext.RequestAborted);
                }
            }, contentType: "text/event-stream");
        }

        var conversation = await WireConversationExecutor.CollectWireConversationResultAsync(
            streamHandle.Events,
            db,
            streamHandle.ConversationId,
            httpContext.RequestAborted);

        if (streamHandle.CreatedConversation)
        {
            await WireConversationExecutor.TryGenerateWireConversationTitleAsync(
                db,
                streamHandle.ConversationId,
                httpContext.RequestAborted);
        }

        if (!string.IsNullOrWhiteSpace(conversation.ErrorPayload))
        {
            return OpenAiWireErrorResults.ProviderNotReady("Provider execution failed for this request.");
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var promptTokens = conversation.PromptTokens;
        var completionTokens = conversation.CompletionTokens;
        var responseId = conversation.ResponseId ?? $"resp_{Guid.NewGuid():N}";
        var assistantText = WireResponseSerializer.RewritePublishedContentUrls(conversation.Text, publicApiOrigin);
        var outputItems = WireResponseSerializer.BuildOpenAiResponsesOutputItems(assistantText, conversation.ExternalToolCalls);
        if (conversation.PendingClientTool && !WireResponseSerializer.ContainsFunctionCallItem(outputItems))
        {
            return OpenAiWireErrorResults.UnsupportedFeature(
                "This request triggered client-side tool execution, but no external tool payload was produced.");
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
