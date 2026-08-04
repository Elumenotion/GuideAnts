using System.Text;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Endpoints.PublishedWire;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.SandboxWireApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Endpoints;

public static class SandboxOpenAiWireHandlers
{
    public static async Task<IResult> GetModelsAsync(
        HttpContext httpContext,
        [FromServices] ISandboxWireExecutionContextResolver executionContextResolver)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            endpointName: "models",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var modelIds = WireModelAliasResolver.BuildEnabledModelAliases(context);
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

    public static async Task<IResult> PostChatCompletionsAsync(
        HttpContext httpContext,
        [FromBody] OpenAiChatCompletionsRequest request,
        [FromServices] ISandboxWireExecutionContextResolver executionContextResolver,
        [FromServices] ISandboxWireConversationService sandboxWireConversationService,
        [FromServices] INotebookFileService notebookFileService,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ApplicationDbContext db)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            endpointName: "chat.completions",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(
            context,
            WireModelAliasResolver.AliasKeys.Guide,
            request.Model);
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

                var resumeStream = sandboxWireConversationService.ResumeAfterExternalToolResultsStreamAsync(
                    context,
                    continuation.ConversationId.Value,
                    clientToolDefinitions,
                    httpContext.RequestAborted);
                streamHandle = new WireConversationExecutor.WireConversationStreamHandle(
                    continuation.ConversationId.Value,
                    CreatedConversation: false,
                    resumeStream);
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

                var ephemeralConversationId = await sandboxWireConversationService.CreateEphemeralConversationAsync(
                    context.NotebookId,
                    httpContext.RequestAborted);

                var attachments = await WireImageAttachmentMaterializer.MaterializeAsync(
                    notebookFileService,
                    context.ProjectId,
                    context.NotebookId,
                    clientPrompt.UserImageUrls,
                    httpClientFactory.CreateClient(),
                    httpContext.RequestAborted);

                var stream = sandboxWireConversationService.SendMessageStreamAsync(
                    context,
                    ephemeralConversationId,
                    new Models.Conversations.SendMessageRequest
                    {
                        Instructions = instructions,
                        Attachments = attachments.Count == 0 ? null : attachments,
                        ClientMessages = clientPrompt.PrefixMessages == null ? null : [.. clientPrompt.PrefixMessages],
                        ClientToolDefinitions = clientToolDefinitions
                    },
                    httpContext.RequestAborted);

                streamHandle = new WireConversationExecutor.WireConversationStreamHandle(
                    ephemeralConversationId,
                    CreatedConversation: true,
                    stream);
            }

            var publicApiOrigin = WireResponseSerializer.ResolvePublicApiOrigin(httpContext);

            if (request.Stream == true)
            {
                var createdStream = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var completionId = $"chatcmpl_{Guid.NewGuid():N}";
                var streamState = new WireStreamAdapter.OpenAiChatStreamState();

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
                }, contentType: "text/event-stream");
            }

            var conversation = await WireConversationExecutor.CollectWireConversationResultAsync(
                streamHandle.Events,
                db,
                streamHandle.ConversationId,
                httpContext.RequestAborted);

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
        catch (InvalidOperationException ex)
        {
            return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status404NotFound,
                ex.Message,
                type: "invalid_request_error",
                code: "conversation_not_found");
        }
    }

    public static async Task<IResult> PostMessagesAsync(
        HttpContext httpContext,
        [FromBody] AnthropicMessagesRequest request,
        [FromServices] ISandboxWireExecutionContextResolver executionContextResolver,
        [FromServices] ISandboxWireConversationService sandboxWireConversationService,
        [FromServices] INotebookFileService notebookFileService,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] ApplicationDbContext db)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            endpointName: "messages",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var configuredAlias = WireModelAliasResolver.ResolveConfiguredAlias(
            context.AliasMap,
            WireModelAliasResolver.AliasKeys.Guide);
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
            WireConversationExecutor.WireConversationStreamHandle streamHandle;
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

                var resumeStream = sandboxWireConversationService.ResumeAfterExternalToolResultsStreamAsync(
                    context,
                    continuation.ConversationId.Value,
                    clientToolDefinitions,
                    httpContext.RequestAborted);
                streamHandle = new WireConversationExecutor.WireConversationStreamHandle(
                    continuation.ConversationId.Value,
                    CreatedConversation: false,
                    resumeStream);
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

                var ephemeralConversationId = await sandboxWireConversationService.CreateEphemeralConversationAsync(
                    context.NotebookId,
                    httpContext.RequestAborted);

                var attachments = await WireImageAttachmentMaterializer.MaterializeAsync(
                    notebookFileService,
                    context.ProjectId,
                    context.NotebookId,
                    clientPrompt.UserImageUrls,
                    httpClientFactory.CreateClient(),
                    httpContext.RequestAborted);

                var stream = sandboxWireConversationService.SendMessageStreamAsync(
                    context,
                    ephemeralConversationId,
                    new Models.Conversations.SendMessageRequest
                    {
                        Instructions = instructions,
                        Attachments = attachments.Count == 0 ? null : attachments,
                        ClientMessages = clientPrompt.PrefixMessages == null ? null : [.. clientPrompt.PrefixMessages],
                        ClientToolDefinitions = clientToolDefinitions
                    },
                    httpContext.RequestAborted);

                streamHandle = new WireConversationExecutor.WireConversationStreamHandle(
                    ephemeralConversationId,
                    CreatedConversation: true,
                    stream);
            }

            var publicApiOrigin = WireResponseSerializer.ResolvePublicApiOrigin(httpContext);

            if (request.Stream == true)
            {
                var streamMessageId = $"msg_{Guid.NewGuid():N}";
                var streamState = new WireStreamAdapter.AnthropicMessagesStreamState();

                return Results.Stream(async outputStream =>
                {
                    await foreach (var sseChunk in WireStreamAdapter.WriteAnthropicMessagesSseAsync(
                                       streamHandle.Events,
                                       streamMessageId,
                                       configuredAlias,
                                       publicApiOrigin,
                                       streamState,
                                       httpContext.RequestAborted))
                    {
                        var bytes = Encoding.UTF8.GetBytes(sseChunk);
                        await outputStream.WriteAsync(bytes, httpContext.RequestAborted);
                        await outputStream.FlushAsync(httpContext.RequestAborted);
                    }
                }, contentType: "text/event-stream");
            }

            var conversation = await WireConversationExecutor.CollectWireConversationResultAsync(
                streamHandle.Events,
                db,
                streamHandle.ConversationId,
                httpContext.RequestAborted);

            if (!string.IsNullOrWhiteSpace(conversation.ErrorPayload))
            {
                return WireResponseSerializer.CreateAnthropicError(
                    StatusCodes.Status503ServiceUnavailable,
                    errorType: "api_error",
                    message: "Provider execution failed for this request.");
            }

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
        catch (KeyNotFoundException ex)
        {
            return WireResponseSerializer.CreateAnthropicError(
                StatusCodes.Status404NotFound,
                errorType: "not_found_error",
                message: ex.Message);
        }
    }

    public static Task<IResult> PostResponsesAsync(
        HttpContext httpContext,
        [FromBody] OpenAiResponsesRequest request,
        [FromServices] ISandboxWireExecutionContextResolver executionContextResolver) =>
        ResolveAndDelegateNotReady(executionContextResolver, httpContext, "responses");

    public static Task<IResult> PostEmbeddingsAsync(
        HttpContext httpContext,
        [FromBody] OpenAiEmbeddingsRequest request,
        [FromServices] ISandboxWireExecutionContextResolver executionContextResolver,
        [FromServices] IEmbeddingService embeddingService,
        [FromServices] IServiceModeResolver serviceModeResolver,
        [FromServices] IPublishedWireUsageRecorder wireUsageRecorder) =>
        SandboxWireMediaHandlers.PostEmbeddingsAsync(
            httpContext,
            request,
            executionContextResolver,
            embeddingService,
            serviceModeResolver,
            wireUsageRecorder);

    public static Task<IResult> PostImageGenerationsAsync(
        HttpContext httpContext,
        [FromBody] OpenAiImageGenerationsRequest request,
        [FromServices] ISandboxWireExecutionContextResolver executionContextResolver,
        [FromServices] INotebookImageService notebookImageService,
        [FromServices] IServiceModeResolver serviceModeResolver,
        [FromServices] IConfiguration configuration,
        [FromServices] IPublishedWireUsageRecorder wireUsageRecorder) =>
        SandboxWireMediaHandlers.PostImageGenerationsAsync(
            httpContext,
            request,
            executionContextResolver,
            notebookImageService,
            serviceModeResolver,
            configuration,
            wireUsageRecorder);

    public static Task<IResult> PostAudioTranscriptionsAsync(
        HttpContext httpContext,
        HttpRequest request,
        [FromServices] ISandboxWireExecutionContextResolver executionContextResolver,
        [FromServices] ISpeechTranscriptionService speechTranscriptionService,
        [FromServices] IServiceModeResolver serviceModeResolver,
        [FromServices] IPublishedWireUsageRecorder wireUsageRecorder) =>
        SandboxWireMediaHandlers.PostAudioTranscriptionsAsync(
            httpContext,
            request,
            executionContextResolver,
            speechTranscriptionService,
            serviceModeResolver,
            wireUsageRecorder);

    public static Task<IResult> PostAudioSpeechAsync(
        HttpContext httpContext,
        [FromBody] OpenAiAudioSpeechRequest request,
        [FromServices] ISandboxWireExecutionContextResolver executionContextResolver,
        [FromServices] ISpeechSynthesisService speechSynthesisService,
        [FromServices] IServiceModeResolver serviceModeResolver,
        [FromServices] IConfiguration configuration,
        [FromServices] IPublishedWireUsageRecorder wireUsageRecorder) =>
        SandboxWireMediaHandlers.PostAudioSpeechAsync(
            httpContext,
            request,
            executionContextResolver,
            speechSynthesisService,
            serviceModeResolver,
            configuration,
            wireUsageRecorder);

    private static async Task<IResult> ResolveAndDelegateNotReady(
        ISandboxWireExecutionContextResolver resolver,
        HttpContext httpContext,
        string endpointName)
    {
        var resolution = await resolver.ResolveAsync(httpContext, endpointName, ct: httpContext.RequestAborted);
        return resolution.Success
            ? OpenAiWireErrorResults.UnsupportedFeature($"{endpointName} is not yet available on sandbox wire API.")
            : resolution.ErrorResult!;
    }
}
