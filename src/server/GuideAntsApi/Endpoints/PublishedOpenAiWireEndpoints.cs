using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Endpoints;

public static class PublishedOpenAiWireEndpoints
{
    public static void MapPublishedOpenAiWireEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/published/openai/{pubId:guid}/v1")
            .WithTags("PublishedOpenAiWire")
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        group.MapGet("/models", PublishedOpenAiWireHandlers.GetModelsAsync);
        group.MapPost("/chat/completions", PublishedOpenAiWireHandlers.PostChatCompletionsAsync);
        group.MapPost("/responses", PublishedOpenAiWireHandlers.PostResponsesAsync);
        group.MapPost("/embeddings", PublishedOpenAiWireHandlers.PostEmbeddingsAsync);
        group.MapPost("/images/generations", PublishedOpenAiWireHandlers.PostImageGenerationsAsync);
        group.MapPost("/audio/transcriptions", PublishedOpenAiWireHandlers.PostAudioTranscriptionsAsync)
            .DisableAntiforgery();
        group.MapPost("/audio/speech", PublishedOpenAiWireHandlers.PostAudioSpeechAsync);
    }
}

public static class PublishedAnthropicWireEndpoints
{
    public static void MapPublishedAnthropicWireEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/published/anthropic/{pubId:guid}/v1")
            .WithTags("PublishedAnthropicWire")
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        group.MapPost("/messages", PublishedOpenAiWireHandlers.PostMessagesAsync);
        group.MapPost("/messages/count_tokens", PublishedOpenAiWireHandlers.PostMessagesCountTokensAsync);
    }
}

public static class PublishedOpenAiWireHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static class AliasKeys
    {
        public const string Guide = "guide";
        public const string Embeddings = "embeddings";
        public const string Image = "image";
        public const string Transcription = "transcription";
        public const string Speech = "speech";
    }

    private sealed record WireConversationResult(
        Guid ConversationId,
        string Text,
        long PromptTokens,
        long CompletionTokens,
        bool PendingClientTool,
        string? ErrorPayload,
        string? ResponseId,
        IReadOnlyList<ChatToolCall> ExternalToolCalls);

    private sealed record ClientPromptParts(
        IReadOnlyList<ChatMessage> PrefixMessages,
        string UserPrompt);

    private sealed record AnthropicContentBlock(
        string Type,
        string? Text = null,
        string? ToolUseId = null,
        string? ToolName = null,
        JsonElement? Input = null);

    private sealed record AnthropicToolResult(
        string ToolCallId,
        string? Name,
        string Content);

    public static async Task<IResult> GetModelsAsync(
        HttpContext httpContext,
        [FromRoute] Guid pubId,
        [FromServices] IPublishedApiExecutionContextResolver executionContextResolver)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            pubId,
            endpointName: "models",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var modelIds = BuildEnabledModelAliases(context.WireApiConfig);
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
        }, JsonOptions);
    }

    public static async Task<IResult> PostChatCompletionsAsync(
        HttpContext httpContext,
        [FromRoute] Guid pubId,
        [FromBody] OpenAiChatCompletionsRequest request,
        [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
        [FromServices] IPublishedConversationService publishedConversationService,
        [FromServices] ApplicationDbContext db)
    {
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
        var modelAlias = ResolveModelAliasOrError(context, AliasKeys.Guide, request.Model);
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        var clientToolDefinitions = ParseOpenAiChatClientToolDefinitions(request.Tools);
        var inboundToolResults = ParseOpenAiChatToolResults(request.Messages);

        try
        {
            WireConversationResult conversation;
            if (inboundToolResults.Count > 0)
            {
                var continuation = await ResolvePendingToolResultConversationAsync(
                    context,
                    db,
                    inboundToolResults,
                    httpContext.RequestAborted);
                if (continuation.ErrorResult != null)
                {
                    return continuation.ErrorResult;
                }

                await AppendAnthropicToolResultsAsync(
                    db,
                    continuation.ConversationId!.Value,
                    inboundToolResults,
                    httpContext.RequestAborted);

                conversation = await ResumeConversationAfterToolResultsAsync(
                    publishedConversationService,
                    db,
                    context,
                    continuation.ConversationId.Value,
                    clientToolDefinitions,
                    httpContext.RequestAborted);
            }
            else
            {
                var clientPrompt = BuildOpenAiChatClientPrompt(request.Messages);
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

                conversation = await ExecuteConversationAsync(
                    publishedConversationService,
                    db,
                    context,
                    instructions,
                    httpContext.RequestAborted,
                    clientMessages: clientPrompt.PrefixMessages,
                    clientToolDefinitions: clientToolDefinitions);
            }

            if (!string.IsNullOrWhiteSpace(conversation.ErrorPayload))
            {
                return OpenAiWireErrorResults.ProviderNotReady("Provider execution failed for this request.");
            }

            var toolCalls = BuildOpenAiChatToolCallsForResponse(conversation.ExternalToolCalls);
            if (conversation.PendingClientTool && toolCalls.Count == 0)
            {
                return OpenAiWireErrorResults.UnsupportedFeature(
                    "This request triggered client-side tool execution, but no external tool payload was produced.");
            }

            var finishReason = conversation.PendingClientTool ? "tool_calls" : "stop";
            var assistantContent = string.IsNullOrWhiteSpace(conversation.Text) ? null : conversation.Text;

            if (request.Stream == true)
            {
                var createdStream = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var completionId = $"chatcmpl_{Guid.NewGuid():N}";
                var ssePayload = BuildOpenAiChatCompletionsSsePayload(
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
            var usage = BuildOpenAiUsage(conversation.PromptTokens, conversation.CompletionTokens);
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
            }, JsonOptions);
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
        var modelAlias = ResolveModelAliasOrError(context, AliasKeys.Guide, request.Model);
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        var clientToolDefinitions = ParseOpenAiResponsesClientToolDefinitions(request.Tools);
        var inboundToolResults = ParseOpenAiResponsesToolResults(request.Input);

        try
        {
            WireConversationResult conversation;
            if (inboundToolResults.Count > 0)
            {
                Guid? resumeConversationId = null;
                if (!string.IsNullOrWhiteSpace(request.PreviousResponseId))
                {
                    var continuation = await ResolveResponsesConversationAsync(
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
                    var continuation = await ResolvePendingToolResultConversationAsync(
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

                await AppendAnthropicToolResultsAsync(
                    db,
                    resumeConversationId!.Value,
                    inboundToolResults,
                    httpContext.RequestAborted);

                conversation = await ResumeConversationAfterToolResultsAsync(
                    publishedConversationService,
                    db,
                    context,
                    resumeConversationId.Value,
                    clientToolDefinitions,
                    httpContext.RequestAborted);
            }
            else
            {
                var clientPrompt = BuildOpenAiResponsesClientPrompt(request.Input);
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

                var conversationResolution = await ResolveResponsesConversationAsync(
                    context,
                    request.PreviousResponseId,
                    db,
                    httpContext.RequestAborted);
                if (conversationResolution.ErrorResult != null)
                {
                    return conversationResolution.ErrorResult;
                }

                conversation = await ExecuteConversationAsync(
                    publishedConversationService,
                    db,
                    context,
                    instructions,
                    httpContext.RequestAborted,
                    existingConversationId: conversationResolution.ConversationId,
                    clientMessages: clientPrompt.PrefixMessages,
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
            var outputItems = BuildOpenAiResponsesOutputItems(conversation.Text, conversation.ExternalToolCalls);
            if (conversation.PendingClientTool && !ContainsFunctionCallItem(outputItems))
            {
                return OpenAiWireErrorResults.UnsupportedFeature(
                    "This request triggered client-side tool execution, but no external tool payload was produced.");
            }

            if (request.Stream == true)
            {
                var ssePayload = BuildOpenAiResponsesSsePayload(
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
            }, JsonOptions);
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

    public static async Task<IResult> PostMessagesAsync(
        HttpContext httpContext,
        [FromRoute] Guid pubId,
        [FromBody] AnthropicMessagesRequest request,
        [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
        [FromServices] IPublishedConversationService publishedConversationService,
        [FromServices] ApplicationDbContext db)
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
        var configuredAlias = ResolveConfiguredAlias(context.WireApiConfig, AliasKeys.Guide);
        if (!string.IsNullOrWhiteSpace(request.Model) &&
            !string.Equals(configuredAlias, request.Model, StringComparison.OrdinalIgnoreCase))
        {
            return CreateAnthropicError(
                StatusCodes.Status400BadRequest,
                errorType: "invalid_request_error",
                message: $"Model alias '{request.Model}' is not configured for this endpoint.");
        }
        var clientToolDefinitions = ParseAnthropicClientToolDefinitions(request.Tools);
        var inboundToolResults = ParseAnthropicToolResults(request.Messages);

        try
        {
            WireConversationResult conversation;
            if (inboundToolResults.Count > 0)
            {
                var continuation = await ResolveAnthropicToolResultConversationAsync(
                    context,
                    db,
                    inboundToolResults,
                    httpContext.RequestAborted);
                if (continuation.ErrorResult != null)
                {
                    return continuation.ErrorResult;
                }

                await AppendAnthropicToolResultsAsync(
                    db,
                    continuation.ConversationId!.Value,
                    inboundToolResults,
                    httpContext.RequestAborted);

                conversation = await ResumeConversationAfterToolResultsAsync(
                    publishedConversationService,
                    db,
                    context,
                    continuation.ConversationId.Value,
                    clientToolDefinitions,
                    httpContext.RequestAborted);
            }
            else
            {
                var clientPrompt = BuildAnthropicClientPrompt(request.System, request.Messages);
                var instructions = clientPrompt.UserPrompt;
                if (string.IsNullOrWhiteSpace(instructions))
                {
                    return CreateAnthropicError(
                        StatusCodes.Status400BadRequest,
                        errorType: "invalid_request_error",
                        message: "At least one textual message is required in 'messages' or 'system'.");
                }

                conversation = await ExecuteConversationAsync(
                    publishedConversationService,
                    db,
                    context,
                    instructions,
                    httpContext.RequestAborted,
                    clientMessages: clientPrompt.PrefixMessages,
                    clientToolDefinitions: clientToolDefinitions);
            }

            if (!string.IsNullOrWhiteSpace(conversation.ErrorPayload))
            {
                return CreateAnthropicError(
                    StatusCodes.Status503ServiceUnavailable,
                    errorType: "api_error",
                    message: "Provider execution failed for this request.");
            }

            var contentBlocks = BuildAnthropicContentBlocks(conversation.Text, conversation.ExternalToolCalls);
            if (conversation.PendingClientTool && contentBlocks.All(b => !string.Equals(b.Type, "tool_use", StringComparison.Ordinal)))
            {
                return CreateAnthropicError(
                    StatusCodes.Status400BadRequest,
                    errorType: "invalid_request_error",
                    message: "This request triggered client-side tool execution, but no external tool payload was produced.");
            }

            var stopReason = conversation.PendingClientTool ? "tool_use" : "end_turn";
            var messageId = $"msg_{Guid.NewGuid():N}";

            if (request.Stream == true)
            {
                var ssePayload = BuildAnthropicMessageSsePayload(
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
                content = contentBlocks.Select(BuildAnthropicResponseContentBlock).ToArray(),
                model = configuredAlias,
                stop_reason = stopReason,
                stop_sequence = (string?)null,
                usage = new
                {
                    input_tokens = conversation.PromptTokens,
                    output_tokens = conversation.CompletionTokens
                }
            }, JsonOptions);
        }
        catch (RoutingException ex)
        {
            return CreateAnthropicError(
                StatusCodes.Status503ServiceUnavailable,
                errorType: "api_error",
                message: ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return CreateAnthropicError(
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
        var configuredAlias = ResolveConfiguredAlias(context.WireApiConfig, AliasKeys.Guide);
        var requestedModel = request.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(requestedModel) &&
            !string.Equals(configuredAlias, requestedModel, StringComparison.OrdinalIgnoreCase))
        {
            return CreateAnthropicError(
                StatusCodes.Status400BadRequest,
                errorType: "invalid_request_error",
                message: $"Model alias '{requestedModel}' is not configured for this endpoint.");
        }

        return Results.Json(new
        {
            input_tokens = EstimateAnthropicInputTokens(request)
        }, JsonOptions);
    }

    public static async Task<IResult> PostEmbeddingsAsync(
        HttpContext httpContext,
        [FromRoute] Guid pubId,
        [FromBody] OpenAiEmbeddingsRequest request,
        [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
        [FromServices] IEmbeddingService embeddingService,
        [FromServices] IServiceModeResolver serviceModeResolver,
        [FromServices] IPublishedWireUsageRecorder wireUsageRecorder)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            pubId,
            endpointName: "embeddings",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var modelAlias = ResolveModelAliasOrError(context, AliasKeys.Embeddings, request.Model);
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        var inputs = ParseEmbeddingsInput(request.Input);
        if (inputs == null || inputs.Count == 0)
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "Input must be a string or string array.",
                type: "invalid_request_error",
                code: "invalid_input",
                param: "input");
        }

        try
        {
            var mode = await serviceModeResolver.ResolveAsync(RoutedServiceNames.Embeddings, modeId: null, httpContext.RequestAborted);
            var vectors = await embeddingService.GetEmbeddingsAsync(inputs, EmbeddingPurpose.Query, httpContext.RequestAborted);
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var data = vectors.Select((embedding, index) => new
            {
                @object = "embedding",
                index,
                embedding
            }).ToArray();

            var inputUnits = inputs.Sum(text => (long)text.Length);
            var outputUnits = vectors.Sum(vector => (long)vector.Length);
            await wireUsageRecorder.RecordAsync(
                context: context,
                category: UsageCategory.Embeddings,
                service: mode.ProviderSection,
                operation: "embeddings",
                metrics: new UsageMetrics(ValueInput: inputUnits, ValueOutput: outputUnits),
                endpoint: "embeddings",
                alias: modelAlias.Alias,
                providerModel: mode.ModelId,
                providerServiceMode: mode.ModeId,
                requestBytes: httpContext.Request.ContentLength,
                inputCount: inputUnits,
                outputCount: outputUnits,
                ct: httpContext.RequestAborted);

            return Results.Json(new
            {
                @object = "list",
                data,
                model = modelAlias.Alias,
                usage = new
                {
                    prompt_tokens = inputUnits,
                    completion_tokens = 0L,
                    total_tokens = inputUnits
                }
            }, JsonOptions);
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

    public static async Task<IResult> PostImageGenerationsAsync(
        HttpContext httpContext,
        [FromRoute] Guid pubId,
        [FromBody] OpenAiImageGenerationsRequest request,
        [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
        [FromServices] INotebookImageService notebookImageService,
        [FromServices] IServiceModeResolver serviceModeResolver,
        [FromServices] IStoragePathResolver storagePathResolver,
        [FromServices] IPublishedWireUsageRecorder wireUsageRecorder)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            pubId,
            endpointName: "images.generations",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var modelAlias = ResolveModelAliasOrError(context, AliasKeys.Image, request.Model);
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "Prompt is required.",
                type: "invalid_request_error",
                code: "invalid_prompt",
                param: "prompt");
        }

        try
        {
            var mode = await serviceModeResolver.ResolveAsync(RoutedServiceNames.ImageGeneration, modeId: null, httpContext.RequestAborted);
            var runContext = new InvocationContext(
                ProjectId: context.ProjectId,
                NotebookId: context.NotebookId,
                ConversationId: Guid.NewGuid())
            {
                IsPublished = true
            };

            var fileName = $"wire-{Guid.NewGuid():N}.png";
            var imageResult = await notebookImageService.GenerateImageAsync(
                prompt: request.Prompt,
                filename: fileName,
                size: string.IsNullOrWhiteSpace(request.Size) ? "1024x1024" : request.Size!,
                n: request.N.GetValueOrDefault(1),
                outputFormat: "png",
                context: runContext);

            var newFile = imageResult.NewFiles?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(newFile))
            {
                return OpenAiWireErrorResults.ProviderNotReady(string.IsNullOrWhiteSpace(imageResult.StandardError)
                    ? "Image generation did not return an output file."
                    : imageResult.StandardError);
            }

            var normalizedRelative = newFile.Trim().Replace("\\", "/").TrimStart('/');
            if (normalizedRelative.StartsWith("../", StringComparison.Ordinal))
            {
                return OpenAiWireErrorResults.ProviderNotReady("Image output path was outside the run directory.");
            }

            var dbRelativePath = $"Runs/{runContext.RunId}/{normalizedRelative}";
            var rootPath = storagePathResolver.GetNotebookRootPath(context.ProjectId, context.NotebookId);
            var fullPath = Path.Combine(rootPath, dbRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                return OpenAiWireErrorResults.ProviderNotReady("Generated image file was not found.");
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, httpContext.RequestAborted);
            var base64 = Convert.ToBase64String(bytes);
            await wireUsageRecorder.RecordAsync(
                context: context,
                category: UsageCategory.ImageGeneration,
                service: mode.ProviderSection,
                operation: "images.generations",
                metrics: new UsageMetrics(ValueInput: request.Prompt.Length, ValueOther: bytes.Length),
                endpoint: "images.generations",
                alias: modelAlias.Alias,
                providerModel: mode.ModelId,
                providerServiceMode: mode.ModeId,
                requestBytes: httpContext.Request.ContentLength,
                inputCount: request.Prompt.Length,
                outputCount: bytes.Length,
                ct: httpContext.RequestAborted);

            return Results.Json(new
            {
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                data = new[]
                {
                    new
                    {
                        b64_json = base64,
                        revised_prompt = request.Prompt
                    }
                }
            }, JsonOptions);
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

    public static async Task<IResult> PostAudioTranscriptionsAsync(
        HttpContext httpContext,
        [FromRoute] Guid pubId,
        [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
        [FromServices] ISpeechTranscriptionService transcriptionService,
        [FromServices] IServiceModeResolver serviceModeResolver,
        [FromServices] IPublishedWireUsageRecorder wireUsageRecorder)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            pubId,
            endpointName: "audio.transcriptions",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        if (!httpContext.Request.HasFormContentType)
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "multipart/form-data content is required.",
                type: "invalid_request_error",
                code: "invalid_content_type");
        }

        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var file = form.Files.GetFile("file") ?? form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
        if (file == null || file.Length == 0)
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "A non-empty audio file is required.",
                type: "invalid_request_error",
                code: "invalid_file",
                param: "file");
        }

        var context = resolution.Context!;
        var modelAlias = ResolveModelAliasOrError(context, AliasKeys.Transcription, form["model"].ToString());
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        if (!transcriptionService.IsAudioFileSupported(file.FileName, file.ContentType))
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "Unsupported audio format.",
                type: "invalid_request_error",
                code: "unsupported_feature",
                param: "file");
        }

        if (!transcriptionService.IsFileSizeSupported(file.Length))
        {
            return OpenAiWireErrorResults.RequestTooLarge("audio.transcriptions", maxBytes: null);
        }

        try
        {
            var mode = await serviceModeResolver.ResolveAsync(RoutedServiceNames.SpeechTranscription, modeId: null, httpContext.RequestAborted);
            using var stream = file.OpenReadStream();
            var result = await transcriptionService.TranscribeAudioWithDurationAsync(
                stream,
                file.FileName,
                file.ContentType ?? "application/octet-stream",
                enableDiarization: false,
                httpContext.RequestAborted);

            await wireUsageRecorder.RecordAsync(
                context: context,
                category: UsageCategory.SpeechTranscription,
                service: mode.ProviderSection,
                operation: "audio.transcriptions",
                metrics: new UsageMetrics(ValueInput: result.DurationSeconds, ValueOutput: result.Text.Length, ValueOther: file.Length),
                endpoint: "audio.transcriptions",
                alias: modelAlias.Alias,
                providerModel: mode.ModelId,
                providerServiceMode: mode.ModeId,
                requestBytes: file.Length,
                inputCount: result.DurationSeconds,
                outputCount: result.Text.Length,
                ct: httpContext.RequestAborted);

            return Results.Json(new
            {
                text = result.Text
            }, JsonOptions);
        }
        catch (RoutingException ex)
        {
            return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
        }
        catch (TimeoutException ex)
        {
            return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
        }
    }

    public static async Task<IResult> PostAudioSpeechAsync(
        HttpContext httpContext,
        [FromRoute] Guid pubId,
        [FromBody] OpenAiAudioSpeechRequest request,
        [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
        [FromServices] ISpeechSynthesisService speechSynthesisService,
        [FromServices] IServiceModeResolver serviceModeResolver,
        [FromServices] IPublishedWireUsageRecorder wireUsageRecorder)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            pubId,
            endpointName: "audio.speech",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var modelAlias = ResolveModelAliasOrError(context, AliasKeys.Speech, request.Model);
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        if (string.IsNullOrWhiteSpace(request.Input))
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "Input text is required.",
                type: "invalid_request_error",
                code: "invalid_input",
                param: "input");
        }

        var responseFormat = string.IsNullOrWhiteSpace(request.ResponseFormat)
            ? "wav"
            : request.ResponseFormat.Trim().ToLowerInvariant();
        if (!string.Equals(responseFormat, "wav", StringComparison.Ordinal))
        {
            return OpenAiWireErrorResults.UnsupportedFeature("Only response_format='wav' is supported.", "response_format");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"wire-speech-{Guid.NewGuid():N}.wav");
        try
        {
            var mode = await serviceModeResolver.ResolveAsync(RoutedServiceNames.SpeechSynthesis, modeId: null, httpContext.RequestAborted);
            var result = await speechSynthesisService.SynthesizeToWavAsync(request.Input, tempPath, httpContext.RequestAborted);
            if (!result.Success)
            {
                return OpenAiWireErrorResults.ProviderNotReady(result.ErrorMessage ?? "Speech synthesis failed.");
            }

            if (!File.Exists(tempPath))
            {
                return OpenAiWireErrorResults.ProviderNotReady("Speech synthesis did not produce an output file.");
            }

            var bytes = await File.ReadAllBytesAsync(tempPath, httpContext.RequestAborted);
            await wireUsageRecorder.RecordAsync(
                context: context,
                category: UsageCategory.SpeechSynthesis,
                service: result.ProviderId ?? mode.ProviderSection,
                operation: "audio.speech",
                metrics: new UsageMetrics(ValueInput: request.Input.Length, ValueOutput: result.DurationSeconds, ValueOther: bytes.Length),
                endpoint: "audio.speech",
                alias: modelAlias.Alias,
                providerModel: mode.ModelId,
                providerServiceMode: mode.ModeId,
                requestBytes: httpContext.Request.ContentLength,
                inputCount: request.Input.Length,
                outputCount: result.DurationSeconds,
                ct: httpContext.RequestAborted);

            return Results.File(bytes, "audio/wav");
        }
        catch (RoutingException ex)
        {
            return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return OpenAiWireErrorResults.ProviderNotReady(ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private static async Task<WireConversationResult> ExecuteConversationAsync(
        IPublishedConversationService publishedConversationService,
        ApplicationDbContext db,
        PublishedApiExecutionContext context,
        string instructions,
        CancellationToken ct,
        Guid? existingConversationId = null,
        IReadOnlyList<ChatMessage>? clientMessages = null,
        IReadOnlyList<ChatToolDefinition>? clientToolDefinitions = null)
    {
        var conversationId = existingConversationId ??
            (await publishedConversationService.CreateConversationAsync(
                context.NotebookId,
                $"wire-{DateTime.UtcNow:yyyyMMddHHmmss}")).Id;
        var request = new SendMessageRequest
        {
            Instructions = instructions,
            ClientMessages = clientMessages == null ? null : [.. clientMessages],
            ClientToolDefinitions = clientToolDefinitions == null ? null : [.. clientToolDefinitions]
        };

        var stream = publishedConversationService.SendMessageStreamAsync(
            conversationId,
            request,
            context.PubId.ToString(),
            context.ExternalUserIdentity,
            context.InternalUserId,
            ct);

        return await CollectWireConversationResultAsync(stream, db, conversationId, ct);
    }

    private static async Task<WireConversationResult> ResumeConversationAfterToolResultsAsync(
        IPublishedConversationService publishedConversationService,
        ApplicationDbContext db,
        PublishedApiExecutionContext context,
        Guid conversationId,
        IReadOnlyList<ChatToolDefinition>? clientToolDefinitions,
        CancellationToken ct)
    {
        var stream = publishedConversationService.ResumeAfterExternalToolResultsStreamAsync(
            conversationId,
            context.PubId.ToString(),
            context.ExternalUserIdentity,
            context.InternalUserId,
            clientToolDefinitions,
            cancellationToken: ct);

        return await CollectWireConversationResultAsync(stream, db, conversationId, ct);
    }

    private static async Task<WireConversationResult> CollectWireConversationResultAsync(
        IAsyncEnumerable<StreamingEvent> stream,
        ApplicationDbContext db,
        Guid conversationId,
        CancellationToken ct)
    {
        var assistantText = new StringBuilder();
        long promptTokens = 0;
        long completionTokens = 0;
        string? errorPayload = null;
        var pendingClientTool = false;
        var externalToolCalls = new List<ChatToolCall>();

        await foreach (var ev in stream)
        {
            if (string.Equals(ev.EventType, StreamingEventTypes.PendingClientTool, StringComparison.Ordinal))
            {
                pendingClientTool = true;
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.ExternalToolCall, StringComparison.Ordinal))
            {
                externalToolCalls.AddRange(ParseExternalToolCallsFromPayload(ev.Payload));
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Error, StringComparison.Ordinal))
            {
                errorPayload = ev.Payload;
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.Usage, StringComparison.Ordinal))
            {
                ReadUsagePayload(ev.Payload, out promptTokens, out completionTokens);
                continue;
            }

            if (string.Equals(ev.EventType, StreamingEventTypes.AssistantMessage, StringComparison.Ordinal) ||
                string.Equals(ev.EventType, StreamingEventTypes.Message, StringComparison.Ordinal))
            {
                var content = ReadContentPayload(ev.Payload);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    assistantText.Clear();
                    assistantText.Append(content);
                }
            }
            else if (string.Equals(ev.EventType, StreamingEventTypes.Token, StringComparison.Ordinal))
            {
                var delta = ReadContentDeltaPayload(ev.Payload);
                if (!string.IsNullOrWhiteSpace(delta) && assistantText.Length == 0)
                {
                    assistantText.Append(delta);
                }
            }
        }

        var latestAssistantMessageId = await ResolveLatestAssistantMessageIdAsync(db, conversationId, ct);
        var responseId = latestAssistantMessageId.HasValue
            ? FormatResponsesId(latestAssistantMessageId.Value)
            : null;

        return new WireConversationResult(
            ConversationId: conversationId,
            Text: assistantText.ToString(),
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            PendingClientTool: pendingClientTool,
            ErrorPayload: errorPayload,
            ResponseId: responseId,
            ExternalToolCalls: externalToolCalls);
    }

    private static IReadOnlyList<ChatToolCall> ParseExternalToolCallsFromPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("toolCalls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<ChatToolCall>>(toolCalls.GetRawText(), JsonOptions) ?? [];
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<ChatToolCall>>(root.GetRawText(), JsonOptions) ?? [];
            }
        }
        catch
        {
            // ignore malformed payloads
        }

        return [];
    }

    private static List<ChatToolDefinition> ParseAnthropicClientToolDefinitions(JsonElement tools)
    {
        var definitions = new List<ChatToolDefinition>();
        if (tools.ValueKind != JsonValueKind.Array)
        {
            return definitions;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object ||
                !tool.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                continue;
            }

            var description = tool.TryGetProperty("description", out var descriptionElement) &&
                              descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;

            JsonNode? inputSchemaNode = null;
            if (tool.TryGetProperty("input_schema", out var inputSchemaElement) &&
                inputSchemaElement.ValueKind != JsonValueKind.Null &&
                inputSchemaElement.ValueKind != JsonValueKind.Undefined)
            {
                try
                {
                    inputSchemaNode = JsonNode.Parse(inputSchemaElement.GetRawText());
                }
                catch
                {
                    inputSchemaNode = null;
                }
            }

            inputSchemaNode ??= JsonNode.Parse("{}");
            var function = new ChatFunctionDefinition(name, description, inputSchemaNode);
            definitions.Add(new ChatToolDefinition(function));
        }

        return definitions;
    }

    private static List<ChatToolDefinition> ParseOpenAiChatClientToolDefinitions(JsonElement tools)
    {
        var definitions = new List<ChatToolDefinition>();
        if (tools.ValueKind != JsonValueKind.Array)
        {
            return definitions;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (tool.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String &&
                !string.Equals(typeElement.GetString(), "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryReadOpenAiFunctionDescriptor(tool, out var name, out var description, out var parameters))
            {
                continue;
            }

            if (!names.Add(name!))
            {
                continue;
            }

            definitions.Add(new ChatToolDefinition(new ChatFunctionDefinition(name!, description, parameters)));
        }

        return definitions;
    }

    private static List<ChatToolDefinition> ParseOpenAiResponsesClientToolDefinitions(JsonElement tools)
    {
        var definitions = new List<ChatToolDefinition>();
        if (tools.ValueKind != JsonValueKind.Array)
        {
            return definitions;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (tool.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String &&
                !string.Equals(typeElement.GetString(), "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryReadOpenAiFunctionDescriptor(tool, out var name, out var description, out var parameters))
            {
                continue;
            }

            if (!names.Add(name!))
            {
                continue;
            }

            definitions.Add(new ChatToolDefinition(new ChatFunctionDefinition(name!, description, parameters)));
        }

        return definitions;
    }

    private static ClientPromptParts BuildOpenAiChatClientPrompt(JsonElement messages)
    {
        var parsedMessages = ParseOpenAiChatClientMessages(messages);
        var fallbackPrompt = BuildInstructionsFromChatMessages(messages);
        return SplitClientPrompt(parsedMessages, fallbackPrompt);
    }

    private static ClientPromptParts BuildOpenAiResponsesClientPrompt(JsonElement input)
    {
        var parsedMessages = ParseOpenAiResponsesClientMessages(input);
        var fallbackPrompt = BuildInstructionsFromResponsesInput(input);
        return SplitClientPrompt(parsedMessages, fallbackPrompt);
    }

    private static ClientPromptParts BuildAnthropicClientPrompt(JsonElement system, JsonElement messages)
    {
        var parsedMessages = ParseAnthropicClientMessages(system, messages);
        var fallbackPrompt = BuildInstructionsFromAnthropicMessages(system, messages);
        return SplitClientPrompt(parsedMessages, fallbackPrompt);
    }

    private static ClientPromptParts SplitClientPrompt(IReadOnlyList<ChatMessage> parsedMessages, string fallbackPrompt)
    {
        var normalizedMessages = parsedMessages
            .Where(IsMeaningfulClientMessage)
            .ToList();

        for (var i = normalizedMessages.Count - 1; i >= 0; i--)
        {
            var message = normalizedMessages[i];
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            var text = message.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var prefixMessages = new List<ChatMessage>(normalizedMessages.Count - 1);
            for (var index = 0; index < normalizedMessages.Count; index++)
            {
                if (index == i)
                {
                    continue;
                }

                prefixMessages.Add(normalizedMessages[index]);
            }

            return new ClientPromptParts(prefixMessages, text.Trim());
        }

        return new ClientPromptParts(normalizedMessages, fallbackPrompt.Trim());
    }

    private static bool IsMeaningfulClientMessage(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.GetText()))
        {
            return true;
        }

        if (message.ToolCalls != null && message.ToolCalls.Count > 0)
        {
            return true;
        }

        return message.Role == ChatRole.Tool && !string.IsNullOrWhiteSpace(message.ToolCallId);
    }

    private static List<ChatMessage> ParseOpenAiChatClientMessages(JsonElement messages)
    {
        var parsedMessages = new List<ChatMessage>();
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return parsedMessages;
        }

        foreach (var item in messages.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var roleText = item.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
                ? roleElement.GetString()
                : "user";
            if (!TryMapClientRole(roleText, out var role))
            {
                continue;
            }

            if (role == ChatRole.Tool)
            {
                var toolCallId = item.TryGetProperty("tool_call_id", out var toolCallIdElement) &&
                                 toolCallIdElement.ValueKind == JsonValueKind.String
                    ? toolCallIdElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(toolCallId))
                {
                    continue;
                }

                var functionName = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : "external_tool";
                var text = item.TryGetProperty("content", out var toolContentElement)
                    ? ExtractTextContent(toolContentElement)
                    : null;
                parsedMessages.Add(new ChatMessage(
                    toolCallId.Trim(),
                    string.IsNullOrWhiteSpace(functionName) ? "external_tool" : functionName.Trim(),
                    CreateTextContents(text)));
                continue;
            }

            var contents = item.TryGetProperty("content", out var contentElement)
                ? CreateTextContents(ExtractTextContent(contentElement))
                : Array.Empty<ChatContent>();

            var toolCalls = role == ChatRole.Assistant &&
                            item.TryGetProperty("tool_calls", out var toolCallsElement)
                ? ParseOpenAiToolCalls(toolCallsElement)
                : [];

            if (toolCalls.Count > 0)
            {
                parsedMessages.Add(new ChatMessage(role, contents, toolCalls));
            }
            else if (contents.Count > 0)
            {
                parsedMessages.Add(new ChatMessage(role, contents));
            }
        }

        return parsedMessages;
    }

    private static List<ChatMessage> ParseOpenAiResponsesClientMessages(JsonElement input)
    {
        var parsedMessages = new List<ChatMessage>();
        if (input.ValueKind == JsonValueKind.String)
        {
            parsedMessages.Add(new ChatMessage(ChatRole.User, CreateTextContents(input.GetString())));
            return parsedMessages;
        }

        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                TryAddOpenAiResponsesClientMessage(item, parsedMessages);
            }

            return parsedMessages;
        }

        TryAddOpenAiResponsesClientMessage(input, parsedMessages);
        return parsedMessages;
    }

    private static void TryAddOpenAiResponsesClientMessage(JsonElement item, ICollection<ChatMessage> destination)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
        {
            var toolCallId = item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String
                ? callIdElement.GetString()
                : item.TryGetProperty("tool_call_id", out var toolCallIdElement) && toolCallIdElement.ValueKind == JsonValueKind.String
                    ? toolCallIdElement.GetString()
                    : item.TryGetProperty("tool_use_id", out var toolUseIdElement) && toolUseIdElement.ValueKind == JsonValueKind.String
                        ? toolUseIdElement.GetString()
                        : null;
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                return;
            }

            var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : "external_tool";
            var text = item.TryGetProperty("output", out var outputElement)
                ? ExtractTextContent(outputElement)
                : item.TryGetProperty("content", out var contentElement)
                    ? ExtractTextContent(contentElement)
                    : null;
            destination.Add(new ChatMessage(
                toolCallId.Trim(),
                string.IsNullOrWhiteSpace(name) ? "external_tool" : name.Trim(),
                CreateTextContents(text)));
            return;
        }

        if (string.Equals(type, "input_text", StringComparison.OrdinalIgnoreCase))
        {
            var text = item.TryGetProperty("text", out var textElement)
                ? ExtractTextContent(textElement)
                : null;
            destination.Add(new ChatMessage(ChatRole.User, CreateTextContents(text)));
            return;
        }

        if (string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
        {
            var text = item.TryGetProperty("text", out var textElement)
                ? ExtractTextContent(textElement)
                : null;
            destination.Add(new ChatMessage(ChatRole.Assistant, CreateTextContents(text)));
            return;
        }

        JsonElement messagePayload = item;
        if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) &&
            item.TryGetProperty("message", out var nestedMessage) &&
            nestedMessage.ValueKind == JsonValueKind.Object)
        {
            messagePayload = nestedMessage;
        }

        var roleText = messagePayload.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
            ? roleElement.GetString()
            : "user";
        if (!TryMapClientRole(roleText, out var role))
        {
            return;
        }

        if (role == ChatRole.Tool)
        {
            var toolCallId = messagePayload.TryGetProperty("tool_call_id", out var toolCallIdElement) &&
                             toolCallIdElement.ValueKind == JsonValueKind.String
                ? toolCallIdElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                return;
            }

            var functionName = messagePayload.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : "external_tool";
            var text = messagePayload.TryGetProperty("content", out var contentElement)
                ? ExtractTextContent(contentElement)
                : null;
            destination.Add(new ChatMessage(
                toolCallId.Trim(),
                string.IsNullOrWhiteSpace(functionName) ? "external_tool" : functionName.Trim(),
                CreateTextContents(text)));
            return;
        }

        var content = messagePayload.TryGetProperty("content", out var messageContentElement)
            ? messageContentElement
            : messagePayload.TryGetProperty("text", out var messageTextElement)
                ? messageTextElement
                : default;
        var contents = content.ValueKind == JsonValueKind.Undefined
            ? Array.Empty<ChatContent>()
            : CreateTextContents(ExtractTextContent(content));

        var toolCalls = role == ChatRole.Assistant &&
                        messagePayload.TryGetProperty("tool_calls", out var toolCallsElement)
            ? ParseOpenAiToolCalls(toolCallsElement)
            : [];

        if (toolCalls.Count > 0)
        {
            destination.Add(new ChatMessage(role, contents, toolCalls));
        }
        else if (contents.Count > 0)
        {
            destination.Add(new ChatMessage(role, contents));
        }
    }

    private static List<ChatMessage> ParseAnthropicClientMessages(JsonElement system, JsonElement messages)
    {
        var parsedMessages = new List<ChatMessage>();

        var systemText = ExtractTextContent(system);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            parsedMessages.Add(new ChatMessage(ChatRole.System, CreateTextContents(systemText)));
        }

        if (messages.ValueKind != JsonValueKind.Array)
        {
            return parsedMessages;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var roleText = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
                ? roleElement.GetString()
                : "user";
            if (!TryMapClientRole(roleText, out var role))
            {
                continue;
            }

            if (!message.TryGetProperty("content", out var content))
            {
                continue;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                var messageText = ExtractTextContent(content);
                var messageContents = CreateTextContents(messageText);
                if (messageContents.Count > 0)
                {
                    parsedMessages.Add(new ChatMessage(role, messageContents));
                }
                continue;
            }

            var textBlocks = new List<string>();
            var toolCalls = new List<ChatToolCall>();
            var toolResults = new List<ChatMessage>();

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var blockType = block.TryGetProperty("type", out var blockTypeElement) && blockTypeElement.ValueKind == JsonValueKind.String
                    ? blockTypeElement.GetString()
                    : null;
                if (string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase))
                {
                    var blockText = ExtractTextContent(block);
                    if (!string.IsNullOrWhiteSpace(blockText))
                    {
                        textBlocks.Add(blockText.Trim());
                    }

                    continue;
                }

                if (role == ChatRole.Assistant &&
                    string.Equals(blockType, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    var toolId = block.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString()
                        : $"call_{Guid.NewGuid():N}";
                    var toolName = block.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : "external_tool";
                    var arguments = block.TryGetProperty("input", out var inputElement)
                        ? NormalizeAnthropicToolInput(inputElement)
                        : JsonSerializer.SerializeToElement(new { });

                    toolCalls.Add(new ChatToolCall
                    {
                        Id = string.IsNullOrWhiteSpace(toolId) ? $"call_{Guid.NewGuid():N}" : toolId.Trim(),
                        Type = "function",
                        Function = new ChatToolCallFunction
                        {
                            Name = string.IsNullOrWhiteSpace(toolName) ? "external_tool" : toolName.Trim(),
                            Arguments = arguments
                        }
                    });

                    continue;
                }

                if (role == ChatRole.User &&
                    string.Equals(blockType, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    var toolId = block.TryGetProperty("tool_use_id", out var toolUseIdElement) &&
                                 toolUseIdElement.ValueKind == JsonValueKind.String
                        ? toolUseIdElement.GetString()
                        : block.TryGetProperty("tool_call_id", out var toolCallIdElement) && toolCallIdElement.ValueKind == JsonValueKind.String
                            ? toolCallIdElement.GetString()
                            : null;
                    if (string.IsNullOrWhiteSpace(toolId))
                    {
                        continue;
                    }

                    var toolName = block.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : "external_tool";
                    var outputText = block.TryGetProperty("content", out var outputElement)
                        ? ExtractTextContent(outputElement)
                        : null;
                    toolResults.Add(new ChatMessage(
                        toolId.Trim(),
                        string.IsNullOrWhiteSpace(toolName) ? "external_tool" : toolName.Trim(),
                        CreateTextContents(outputText)));
                }
            }

            var text = textBlocks.Count == 0 ? null : string.Join("\n", textBlocks);
            var contents = CreateTextContents(text);

            if (role == ChatRole.Assistant && toolCalls.Count > 0)
            {
                parsedMessages.Add(new ChatMessage(role, contents, toolCalls));
            }
            else if (contents.Count > 0)
            {
                parsedMessages.Add(new ChatMessage(role, contents));
            }

            if (toolResults.Count > 0)
            {
                parsedMessages.AddRange(toolResults);
            }
        }

        return parsedMessages;
    }

    private static IReadOnlyList<ChatToolCall> ParseOpenAiToolCalls(JsonElement toolCallsElement)
    {
        var toolCalls = new List<ChatToolCall>();
        if (toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return toolCalls;
        }

        foreach (var toolCallElement in toolCallsElement.EnumerateArray())
        {
            if (toolCallElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = toolCallElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : "function";
            if (!string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = toolCallElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : $"call_{Guid.NewGuid():N}";
            if (!toolCallElement.TryGetProperty("function", out var functionElement) || functionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = functionElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            JsonElement arguments = JsonSerializer.SerializeToElement(new { });
            if (functionElement.TryGetProperty("arguments", out var argumentsElement))
            {
                arguments = NormalizeAnthropicToolInput(argumentsElement);
            }

            toolCalls.Add(new ChatToolCall
            {
                Id = string.IsNullOrWhiteSpace(id) ? $"call_{Guid.NewGuid():N}" : id.Trim(),
                Type = "function",
                Function = new ChatToolCallFunction
                {
                    Name = name.Trim(),
                    Arguments = arguments
                }
            });
        }

        return toolCalls;
    }

    private static IReadOnlyList<ChatContent> CreateTextContents(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<ChatContent>();
        }

        return [new ChatContent(text.Trim())];
    }

    private static bool TryMapClientRole(string? role, out ChatRole mappedRole)
    {
        mappedRole = ChatRole.User;
        if (string.IsNullOrWhiteSpace(role))
        {
            return true;
        }

        switch (role.Trim().ToLowerInvariant())
        {
            case "system":
                mappedRole = ChatRole.System;
                return true;
            case "developer":
                mappedRole = ChatRole.Developer;
                return true;
            case "user":
                mappedRole = ChatRole.User;
                return true;
            case "assistant":
                mappedRole = ChatRole.Assistant;
                return true;
            case "tool":
                mappedRole = ChatRole.Tool;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadOpenAiFunctionDescriptor(
        JsonElement tool,
        out string? name,
        out string? description,
        out JsonNode? parameters)
    {
        name = null;
        description = null;
        parameters = JsonNode.Parse("{}");

        JsonElement descriptor;
        if (tool.TryGetProperty("function", out var functionElement) &&
            functionElement.ValueKind == JsonValueKind.Object)
        {
            descriptor = functionElement;
        }
        else
        {
            descriptor = tool;
        }

        if (!descriptor.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        name = nameElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (descriptor.TryGetProperty("description", out var descriptionElement) &&
            descriptionElement.ValueKind == JsonValueKind.String)
        {
            description = descriptionElement.GetString();
        }

        if (descriptor.TryGetProperty("parameters", out var parametersElement) &&
            parametersElement.ValueKind != JsonValueKind.Null &&
            parametersElement.ValueKind != JsonValueKind.Undefined)
        {
            try
            {
                parameters = JsonNode.Parse(parametersElement.GetRawText());
            }
            catch
            {
                parameters = JsonNode.Parse("{}");
            }
        }
        else if (descriptor.TryGetProperty("input_schema", out var inputSchemaElement) &&
                 inputSchemaElement.ValueKind != JsonValueKind.Null &&
                 inputSchemaElement.ValueKind != JsonValueKind.Undefined)
        {
            try
            {
                parameters = JsonNode.Parse(inputSchemaElement.GetRawText());
            }
            catch
            {
                parameters = JsonNode.Parse("{}");
            }
        }

        return true;
    }

    private static List<AnthropicToolResult> ParseOpenAiChatToolResults(JsonElement messages)
    {
        var results = new List<AnthropicToolResult>();
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("role", out var roleElement) ||
                roleElement.ValueKind != JsonValueKind.String ||
                !string.Equals(roleElement.GetString(), "tool", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var toolCallId = message.TryGetProperty("tool_call_id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                continue;
            }

            var name = message.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var content = message.TryGetProperty("content", out var contentElement)
                ? ExtractTextContent(contentElement) ?? string.Empty
                : string.Empty;

            results.Add(new AnthropicToolResult(toolCallId.Trim(), name, content));
        }

        return results;
    }

    private static List<AnthropicToolResult> ParseOpenAiResponsesToolResults(JsonElement input)
    {
        var results = new List<AnthropicToolResult>();
        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                TryAddOpenAiResponsesToolResult(item, results);
            }
        }
        else
        {
            TryAddOpenAiResponsesToolResult(input, results);
        }

        return results;
    }

    private static void TryAddOpenAiResponsesToolResult(JsonElement item, ICollection<AnthropicToolResult> destination)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        if (!string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var toolCallId = item.TryGetProperty("call_id", out var callIdElement) && callIdElement.ValueKind == JsonValueKind.String
            ? callIdElement.GetString()
            : item.TryGetProperty("tool_call_id", out var toolCallIdElement) && toolCallIdElement.ValueKind == JsonValueKind.String
                ? toolCallIdElement.GetString()
                : item.TryGetProperty("tool_use_id", out var toolUseIdElement) && toolUseIdElement.ValueKind == JsonValueKind.String
                    ? toolUseIdElement.GetString()
                    : null;
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        var content = item.TryGetProperty("output", out var outputElement)
            ? ExtractTextContent(outputElement) ?? string.Empty
            : item.TryGetProperty("content", out var contentElement)
                ? ExtractTextContent(contentElement) ?? string.Empty
                : string.Empty;

        destination.Add(new AnthropicToolResult(toolCallId.Trim(), name, content));
    }

    private static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolvePendingToolResultConversationAsync(
        PublishedApiExecutionContext context,
        ApplicationDbContext db,
        IReadOnlyList<AnthropicToolResult> toolResults,
        CancellationToken ct)
    {
        var toolCallIds = toolResults
            .Select(r => r.ToolCallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (toolCallIds.Length == 0)
        {
            return (null, OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "Tool results must include at least one tool call id.",
                type: "invalid_request_error",
                code: "invalid_tool_results",
                param: "input"));
        }

        var assistantQuery = db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversation.NotebookId == context.NotebookId &&
                m.Role == DataModelChatRole.Assistant &&
                m.ToolCalls != null);
        foreach (var toolCallId in toolCallIds)
        {
            assistantQuery = assistantQuery.Where(m => m.ToolCalls!.Contains(toolCallId));
        }

        var pendingAssistant = await assistantQuery
            .OrderByDescending(m => m.Created)
            .Select(m => new
            {
                m.NotebookConversationId,
                m.TurnIndex
            })
            .FirstOrDefaultAsync(ct);

        if (pendingAssistant == null)
        {
            return (null, OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "No pending tool invocation matches the supplied tool call ids.",
                type: "invalid_request_error",
                code: "tool_results_not_found",
                param: "input"));
        }

        var turn = await db.ConversationTurns
            .AsNoTracking()
            .Where(t =>
                t.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                t.TurnIndex == pendingAssistant.TurnIndex)
            .Select(t => new { t.Status })
            .FirstOrDefaultAsync(ct);

        if (turn == null || !string.Equals(turn.Status, "streaming", StringComparison.OrdinalIgnoreCase))
        {
            return (null, OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "The referenced tool invocation is no longer pending.",
                type: "invalid_request_error",
                code: "tool_results_not_pending",
                param: "input"));
        }

        if (context.InternalUserId.HasValue)
        {
            var matchesUser = await db.NotebookConversationMessages
                .AsNoTracking()
                .Where(m =>
                    m.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                    m.TurnIndex == pendingAssistant.TurnIndex &&
                    m.Role == DataModelChatRole.User)
                .AnyAsync(m => m.UserId == context.InternalUserId, ct);
            if (!matchesUser)
            {
                return (null, OpenAiWireErrorResults.Create(
                    StatusCodes.Status404NotFound,
                    "No matching pending tool invocation was found for this user.",
                    type: "not_found_error",
                    code: "tool_results_not_found",
                    param: "input"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(context.ExternalUserIdentity))
        {
            var matchesIdentity = await db.NotebookConversationMessages
                .AsNoTracking()
                .Where(m =>
                    m.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                    m.TurnIndex == pendingAssistant.TurnIndex &&
                    m.Role == DataModelChatRole.User)
                .AnyAsync(m => m.ExternalUserIdentity == context.ExternalUserIdentity, ct);
            if (!matchesIdentity)
            {
                return (null, OpenAiWireErrorResults.Create(
                    StatusCodes.Status404NotFound,
                    "No matching pending tool invocation was found for this user.",
                    type: "not_found_error",
                    code: "tool_results_not_found",
                    param: "input"));
            }
        }

        return (pendingAssistant.NotebookConversationId, null);
    }

    private static List<AnthropicToolResult> ParseAnthropicToolResults(JsonElement messages)
    {
        var results = new List<AnthropicToolResult>();
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var contentElement))
            {
                continue;
            }

            if (contentElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentElement.EnumerateArray())
                {
                    TryAddAnthropicToolResult(block, results);
                }
            }
            else
            {
                TryAddAnthropicToolResult(contentElement, results);
            }
        }

        return results;
    }

    private static void TryAddAnthropicToolResult(JsonElement block, ICollection<AnthropicToolResult> destination)
    {
        if (block.ValueKind != JsonValueKind.Object ||
            !block.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(typeElement.GetString(), "tool_result", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var toolCallId = block.TryGetProperty("tool_use_id", out var toolUseIdElement) &&
                         toolUseIdElement.ValueKind == JsonValueKind.String
            ? toolUseIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        var name = block.TryGetProperty("name", out var nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        var content = block.TryGetProperty("content", out var contentElement)
            ? ExtractTextContent(contentElement) ?? string.Empty
            : string.Empty;

        destination.Add(new AnthropicToolResult(toolCallId.Trim(), name, content));
    }

    private static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolveAnthropicToolResultConversationAsync(
        PublishedApiExecutionContext context,
        ApplicationDbContext db,
        IReadOnlyList<AnthropicToolResult> toolResults,
        CancellationToken ct)
    {
        var toolCallIds = toolResults
            .Select(r => r.ToolCallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (toolCallIds.Length == 0)
        {
            return (null, CreateAnthropicError(
                StatusCodes.Status400BadRequest,
                errorType: "invalid_request_error",
                message: "Tool results must include at least one 'tool_use_id'."));
        }

        var assistantQuery = db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversation.NotebookId == context.NotebookId &&
                m.Role == DataModelChatRole.Assistant &&
                m.ToolCalls != null);
        foreach (var toolCallId in toolCallIds)
        {
            assistantQuery = assistantQuery.Where(m => m.ToolCalls!.Contains(toolCallId));
        }

        var pendingAssistant = await assistantQuery
            .OrderByDescending(m => m.Created)
            .Select(m => new
            {
                m.NotebookConversationId,
                m.TurnIndex
            })
            .FirstOrDefaultAsync(ct);

        if (pendingAssistant == null)
        {
            return (null, CreateAnthropicError(
                StatusCodes.Status400BadRequest,
                errorType: "invalid_request_error",
                message: "No pending tool invocation matches the supplied tool_use_id values."));
        }

        var turn = await db.ConversationTurns
            .AsNoTracking()
            .Where(t =>
                t.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                t.TurnIndex == pendingAssistant.TurnIndex)
            .Select(t => new { t.Status })
            .FirstOrDefaultAsync(ct);

        if (turn == null || !string.Equals(turn.Status, "streaming", StringComparison.OrdinalIgnoreCase))
        {
            return (null, CreateAnthropicError(
                StatusCodes.Status400BadRequest,
                errorType: "invalid_request_error",
                message: "The referenced tool invocation is no longer pending."));
        }

        if (context.InternalUserId.HasValue)
        {
            var matchesUser = await db.NotebookConversationMessages
                .AsNoTracking()
                .Where(m =>
                    m.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                    m.TurnIndex == pendingAssistant.TurnIndex &&
                    m.Role == DataModelChatRole.User)
                .AnyAsync(m => m.UserId == context.InternalUserId, ct);
            if (!matchesUser)
            {
                return (null, CreateAnthropicError(
                    StatusCodes.Status404NotFound,
                    errorType: "not_found_error",
                    message: "No matching pending tool invocation was found for this user."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(context.ExternalUserIdentity))
        {
            var matchesIdentity = await db.NotebookConversationMessages
                .AsNoTracking()
                .Where(m =>
                    m.NotebookConversationId == pendingAssistant.NotebookConversationId &&
                    m.TurnIndex == pendingAssistant.TurnIndex &&
                    m.Role == DataModelChatRole.User)
                .AnyAsync(m => m.ExternalUserIdentity == context.ExternalUserIdentity, ct);
            if (!matchesIdentity)
            {
                return (null, CreateAnthropicError(
                    StatusCodes.Status404NotFound,
                    errorType: "not_found_error",
                    message: "No matching pending tool invocation was found for this user."));
            }
        }

        return (pendingAssistant.NotebookConversationId, null);
    }

    private static async Task AppendAnthropicToolResultsAsync(
        ApplicationDbContext db,
        Guid conversationId,
        IReadOnlyList<AnthropicToolResult> toolResults,
        CancellationToken ct)
    {
        var conversation = await db.NotebookConversations
            .Include(c => c.Turns)
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new InvalidOperationException("Conversation not found.");

        var turn = conversation.Turns
            .OrderByDescending(t => t.TurnIndex)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No active turn to append tool results.");

        var existingToolCallIds = await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turn.TurnIndex && m.ToolCallId != null)
            .Select(m => m.ToolCallId!)
            .ToListAsync(ct);

        var nextSequence = (await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turn.TurnIndex)
                .MaxAsync(m => (int?)m.MessageSequence, ct) ?? 1) + 1;

        var now = DateTime.UtcNow;
        foreach (var result in toolResults)
        {
            if (existingToolCallIds.Any(id => string.Equals(id, result.ToolCallId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            db.NotebookConversationMessages.Add(new GuideAntsApi.DataModel.Models.NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = turn.TurnIndex,
                MessageSequence = nextSequence++,
                Role = DataModelChatRole.Tool,
                Content = result.Content ?? string.Empty,
                ToolCallId = result.ToolCallId,
                FunctionName = result.Name ?? "external_tool",
                Created = now
            });
        }

        turn.Status = "streaming";
        turn.LastUpdated = now;
        await db.SaveChangesAsync(ct);
    }

    private static List<AnthropicContentBlock> BuildAnthropicContentBlocks(
        string text,
        IReadOnlyList<ChatToolCall> externalToolCalls)
    {
        var blocks = new List<AnthropicContentBlock>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(new AnthropicContentBlock("text", Text: text));
        }

        foreach (var toolCall in externalToolCalls)
        {
            if (string.IsNullOrWhiteSpace(toolCall.Function?.Name))
            {
                continue;
            }

            var toolUseId = string.IsNullOrWhiteSpace(toolCall.Id)
                ? $"toolu_{Guid.NewGuid():N}"
                : toolCall.Id;
            var input = NormalizeAnthropicToolInput(toolCall.Function.Arguments);
            blocks.Add(new AnthropicContentBlock(
                "tool_use",
                ToolUseId: toolUseId,
                ToolName: toolCall.Function.Name,
                Input: input));
        }

        if (blocks.Count == 0 && externalToolCalls.Count == 0)
        {
            blocks.Add(new AnthropicContentBlock("text", Text: string.Empty));
        }

        return blocks;
    }

    private static List<Dictionary<string, object?>> BuildOpenAiChatToolCallsForResponse(
        IReadOnlyList<ChatToolCall> externalToolCalls)
    {
        var toolCalls = new List<Dictionary<string, object?>>();
        foreach (var toolCall in externalToolCalls)
        {
            if (!toolCall.IsFunction || string.IsNullOrWhiteSpace(toolCall.Function?.Name))
            {
                continue;
            }

            var toolCallId = string.IsNullOrWhiteSpace(toolCall.Id)
                ? $"call_{Guid.NewGuid():N}"
                : toolCall.Id;
            toolCalls.Add(new Dictionary<string, object?>
            {
                ["id"] = toolCallId,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = toolCall.Function.Name,
                    ["arguments"] = NormalizeOpenAiFunctionArguments(toolCall.Function.Arguments)
                }
            });
        }

        return toolCalls;
    }

    private static List<Dictionary<string, object?>> BuildOpenAiResponsesOutputItems(
        string text,
        IReadOnlyList<ChatToolCall> externalToolCalls)
    {
        var outputItems = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            outputItems.Add(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["id"] = $"msg_{Guid.NewGuid():N}",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new[]
                {
                    new
                    {
                        type = "output_text",
                        text,
                        annotations = Array.Empty<object>()
                    }
                }
            });
        }

        foreach (var toolCall in externalToolCalls)
        {
            if (!toolCall.IsFunction || string.IsNullOrWhiteSpace(toolCall.Function?.Name))
            {
                continue;
            }

            var callId = string.IsNullOrWhiteSpace(toolCall.Id)
                ? $"call_{Guid.NewGuid():N}"
                : toolCall.Id;
            outputItems.Add(new Dictionary<string, object?>
            {
                ["type"] = "function_call",
                ["id"] = $"fc_{Guid.NewGuid():N}",
                ["call_id"] = callId,
                ["name"] = toolCall.Function.Name,
                ["arguments"] = NormalizeOpenAiFunctionArguments(toolCall.Function.Arguments),
                ["status"] = "completed"
            });
        }

        if (outputItems.Count == 0)
        {
            outputItems.Add(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["id"] = $"msg_{Guid.NewGuid():N}",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = string.Empty,
                        annotations = Array.Empty<object>()
                    }
                }
            });
        }

        return outputItems;
    }

    private static bool ContainsFunctionCallItem(IEnumerable<Dictionary<string, object?>> outputItems)
    {
        return outputItems.Any(item =>
            item.TryGetValue("type", out var type) &&
            type is string typeString &&
            string.Equals(typeString, "function_call", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeOpenAiFunctionArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object || arguments.ValueKind == JsonValueKind.Array)
        {
            return arguments.GetRawText();
        }

        if (arguments.ValueKind == JsonValueKind.String)
        {
            var raw = arguments.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "{}";
            }

            try
            {
                using var parsed = JsonDocument.Parse(raw);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object ||
                    parsed.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return parsed.RootElement.GetRawText();
                }
            }
            catch
            {
                // fall back to wrapping invalid JSON strings
            }

            return JsonSerializer.Serialize(new { value = raw });
        }

        if (arguments.ValueKind == JsonValueKind.Null || arguments.ValueKind == JsonValueKind.Undefined)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(new { value = arguments.GetRawText() });
    }

    private static object BuildAnthropicResponseContentBlock(AnthropicContentBlock block)
    {
        if (string.Equals(block.Type, "tool_use", StringComparison.OrdinalIgnoreCase))
        {
            var input = block.Input ?? JsonSerializer.SerializeToElement(new { });
            return new
            {
                type = "tool_use",
                id = block.ToolUseId,
                name = block.ToolName,
                input
            };
        }

        return new
        {
            type = "text",
            text = block.Text ?? string.Empty
        };
    }

    private static JsonElement NormalizeAnthropicToolInput(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object || arguments.ValueKind == JsonValueKind.Array)
        {
            return arguments.Clone();
        }

        if (arguments.ValueKind == JsonValueKind.String)
        {
            var raw = arguments.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var parsed = JsonDocument.Parse(raw);
                    if (parsed.RootElement.ValueKind == JsonValueKind.Object ||
                        parsed.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return parsed.RootElement.Clone();
                    }
                }
                catch
                {
                    return JsonSerializer.SerializeToElement(new { value = raw });
                }

                return JsonSerializer.SerializeToElement(new { value = raw });
            }
        }

        if (arguments.ValueKind == JsonValueKind.Null || arguments.ValueKind == JsonValueKind.Undefined)
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        return JsonSerializer.SerializeToElement(new { value = arguments.GetRawText() });
    }

    private static async Task<(Guid? ConversationId, IResult? ErrorResult)> ResolveResponsesConversationAsync(
        PublishedApiExecutionContext context,
        string? previousResponseId,
        ApplicationDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(previousResponseId))
        {
            return (null, null);
        }

        if (!TryParseResponsesMessageId(previousResponseId, out var previousMessageId))
        {
            return (null, OpenAiWireErrorResults.InvalidPreviousResponseId(previousResponseId));
        }

        var previousAssistant = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.Id == previousMessageId && m.Role == DataModelChatRole.Assistant)
            .Select(m => new
            {
                m.Id,
                m.NotebookConversationId,
                m.TurnIndex,
                NotebookId = m.NotebookConversation.NotebookId
            })
            .FirstOrDefaultAsync(ct);

        if (previousAssistant == null || previousAssistant.NotebookId != context.NotebookId)
        {
            return (null, OpenAiWireErrorResults.PreviousResponseNotFound(previousResponseId));
        }

        var turnUserIdentity = await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == previousAssistant.NotebookConversationId &&
                m.TurnIndex == previousAssistant.TurnIndex &&
                m.Role == DataModelChatRole.User)
            .OrderBy(m => m.MessageSequence)
            .Select(m => new
            {
                m.UserId,
                m.ExternalUserIdentity
            })
            .FirstOrDefaultAsync(ct);

        if (context.InternalUserId.HasValue)
        {
            if (turnUserIdentity?.UserId != context.InternalUserId)
            {
                return (null, OpenAiWireErrorResults.PreviousResponseScopeMismatch());
            }
        }
        else if (!string.IsNullOrWhiteSpace(context.ExternalUserIdentity))
        {
            if (!string.Equals(
                    turnUserIdentity?.ExternalUserIdentity,
                    context.ExternalUserIdentity,
                    StringComparison.Ordinal))
            {
                return (null, OpenAiWireErrorResults.PreviousResponseScopeMismatch());
            }
        }

        var latestAssistantMessageId = await ResolveLatestAssistantMessageIdAsync(
            db,
            previousAssistant.NotebookConversationId,
            ct);
        if (!latestAssistantMessageId.HasValue)
        {
            return (null, OpenAiWireErrorResults.PreviousResponseNotFound(previousResponseId));
        }

        if (latestAssistantMessageId.Value != previousAssistant.Id)
        {
            return (null, OpenAiWireErrorResults.UnsupportedPreviousResponseBranch());
        }

        return (previousAssistant.NotebookConversationId, null);
    }

    private static async Task<Guid?> ResolveLatestAssistantMessageIdAsync(
        ApplicationDbContext db,
        Guid conversationId,
        CancellationToken ct)
    {
        return await db.NotebookConversationMessages
            .AsNoTracking()
            .Where(m =>
                m.NotebookConversationId == conversationId &&
                m.Role == DataModelChatRole.Assistant &&
                m.IsStreaming != true)
            .OrderByDescending(m => m.TurnIndex)
            .ThenByDescending(m => m.MessageSequence)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string FormatResponsesId(Guid messageId) => $"resp_{messageId:N}";

    private static bool TryParseResponsesMessageId(string responseId, out Guid messageId)
    {
        messageId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(responseId))
        {
            return false;
        }

        var trimmed = responseId.Trim();
        const string prefix = "resp_";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawId = trimmed[prefix.Length..];
        return Guid.TryParse(rawId, out messageId);
    }

    private static (string Alias, IResult? ErrorResult) ResolveModelAliasOrError(
        PublishedApiExecutionContext context,
        string aliasKey,
        string? requestedModel)
    {
        var configuredAlias = ResolveConfiguredAlias(context.WireApiConfig, aliasKey);
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return (configuredAlias, null);
        }

        if (string.Equals(configuredAlias, requestedModel, StringComparison.OrdinalIgnoreCase))
        {
            return (configuredAlias, null);
        }

        return (configuredAlias, OpenAiWireErrorResults.MissingModelAlias(requestedModel));
    }

    private static IReadOnlyList<string> BuildEnabledModelAliases(PublishedWireApiConfigDto config)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flags = config.EndpointFlags ?? new PublishedWireApiEndpointFlagsDto();

        if (flags.ChatCompletions != false || flags.Responses != false || flags.Messages != false)
        {
            aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Guide));
        }
        if (flags.Embeddings != false)
        {
            aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Embeddings));
        }
        if (flags.ImageGenerations != false)
        {
            aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Image));
        }
        if (flags.AudioTranscriptions != false)
        {
            aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Transcription));
        }
        if (flags.AudioSpeech != false)
        {
            aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Speech));
        }

        return aliases.OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ResolveConfiguredAlias(PublishedWireApiConfigDto config, string aliasKey)
    {
        var alias = aliasKey;
        if (config.AliasMap != null &&
            config.AliasMap.TryGetValue(aliasKey, out var configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            alias = configured.Trim();
        }

        return alias;
    }

    private static string BuildInstructionsFromAnthropicMessages(JsonElement system, JsonElement messages)
    {
        var builder = new StringBuilder();

        if (system.ValueKind != JsonValueKind.Undefined &&
            system.ValueKind != JsonValueKind.Null)
        {
            var systemText = ExtractTextContent(system);
            if (!string.IsNullOrWhiteSpace(systemText))
            {
                builder.Append("system: ").AppendLine(systemText.Trim());
            }
        }

        var messageText = BuildInstructionsFromChatMessages(messages);
        if (!string.IsNullOrWhiteSpace(messageText))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(messageText.Trim());
        }

        return builder.ToString().Trim();
    }

    private static string BuildInstructionsFromChatMessages(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
                ? roleElement.GetString()
                : "user";
            var content = message.TryGetProperty("content", out var contentElement)
                ? ExtractTextContent(contentElement)
                : null;

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            builder.Append(role).Append(": ").AppendLine(content.Trim());
        }

        return builder.ToString().Trim();
    }

    private static string BuildInstructionsFromResponsesInput(JsonElement input)
    {
        return ExtractTextContent(input)?.Trim() ?? string.Empty;
    }

    private static List<string>? ParseEmbeddingsInput(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            var value = input.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : new List<string> { value };
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>();
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static string? ExtractTextContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var item in element.EnumerateArray())
            {
                var text = ExtractTextContent(item);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }
                builder.Append(text.Trim());
            }

            return builder.ToString();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                return textElement.GetString();
            }

            if (element.TryGetProperty("content", out var contentElement))
            {
                return ExtractTextContent(contentElement);
            }
        }

        return null;
    }

    private static string ExtractTextFromResponseMessageItem(Dictionary<string, object?> item)
    {
        if (!item.TryGetValue("content", out var contentValue) || contentValue == null)
        {
            return string.Empty;
        }

        try
        {
            var raw = JsonSerializer.Serialize(contentValue, JsonOptions);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var contentItem in doc.RootElement.EnumerateArray())
            {
                if (contentItem.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = contentItem.TryGetProperty("type", out var typeElement) &&
                           typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
                if (!string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (contentItem.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    return textElement.GetString() ?? string.Empty;
                }
            }
        }
        catch
        {
            // best effort
        }

        return string.Empty;
    }

    private static void ReadUsagePayload(string payload, out long promptTokens, out long completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            promptTokens = ReadLongProperty(root, "promptTokens", "prompt_tokens");
            completionTokens = ReadLongProperty(root, "completionTokens", "completion_tokens");
        }
        catch (JsonException)
        {
            // best effort usage parsing
        }
    }

    private static string? ReadContentPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore malformed content payload
        }

        return null;
    }

    private static string? ReadContentDeltaPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("contentDelta", out var delta) && delta.ValueKind == JsonValueKind.String)
            {
                return delta.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore malformed delta payload
        }

        return null;
    }

    private static long ReadLongProperty(JsonElement root, string camelName, string snakeName)
    {
        if (root.TryGetProperty(camelName, out var camelValue) && camelValue.ValueKind == JsonValueKind.Number && camelValue.TryGetInt64(out var camelLong))
        {
            return camelLong;
        }

        if (root.TryGetProperty(snakeName, out var snakeValue) && snakeValue.ValueKind == JsonValueKind.Number && snakeValue.TryGetInt64(out var snakeLong))
        {
            return snakeLong;
        }

        return 0;
    }

    private static object BuildOpenAiUsage(long promptTokens, long completionTokens) =>
        new
        {
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
            total_tokens = promptTokens + completionTokens
        };

    private static string BuildOpenAiChatCompletionsSsePayload(
        string completionId,
        string modelAlias,
        long created,
        string? text,
        IReadOnlyList<Dictionary<string, object?>> toolCalls,
        string finishReason,
        long promptTokens,
        long completionTokens)
    {
        var builder = new StringBuilder();

        void AppendData(object payload)
        {
            builder.Append("data: ")
                .Append(JsonSerializer.Serialize(payload, JsonOptions))
                .Append("\n\n");
        }

        object delta;
        if (!string.IsNullOrWhiteSpace(text) && toolCalls.Count > 0)
        {
            delta = new
            {
                role = "assistant",
                content = text,
                tool_calls = toolCalls
            };
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            delta = new
            {
                role = "assistant",
                content = text
            };
        }
        else if (toolCalls.Count > 0)
        {
            delta = new
            {
                role = "assistant",
                tool_calls = toolCalls
            };
        }
        else
        {
            delta = new { role = "assistant" };
        }

        AppendData(new
        {
            id = completionId,
            @object = "chat.completion.chunk",
            created,
            model = modelAlias,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta,
                    finish_reason = (string?)null
                }
            }
        });

        AppendData(new
        {
            id = completionId,
            @object = "chat.completion.chunk",
            created,
            model = modelAlias,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new Dictionary<string, object?>(),
                    finish_reason = finishReason
                }
            }
        });

        AppendData(new
        {
            id = completionId,
            @object = "chat.completion.chunk",
            created,
            model = modelAlias,
            choices = Array.Empty<object>(),
            usage = BuildOpenAiUsage(promptTokens, completionTokens)
        });

        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

    private static string BuildOpenAiResponsesSsePayload(
        string responseId,
        string modelAlias,
        long created,
        IReadOnlyList<Dictionary<string, object?>> outputItems,
        long promptTokens,
        long completionTokens)
    {
        var builder = new StringBuilder();

        void AppendEvent(string eventType, object payload)
        {
            builder.Append("event: ")
                .Append(eventType)
                .Append('\n')
                .Append("data: ")
                .Append(JsonSerializer.Serialize(payload, JsonOptions))
                .Append("\n\n");
        }

        AppendEvent("response.created", new
        {
            type = "response.created",
            response = new
            {
                id = responseId,
                @object = "response",
                created,
                status = "in_progress",
                model = modelAlias,
                output = Array.Empty<object>()
            }
        });

        for (var index = 0; index < outputItems.Count; index++)
        {
            var item = outputItems[index];
            var itemType = item.TryGetValue("type", out var typeValue) ? typeValue as string : null;

            if (string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase))
            {
                var messageId = item.TryGetValue("id", out var idValue) && idValue is string id
                    ? id
                    : $"msg_{Guid.NewGuid():N}";
                var text = ExtractTextFromResponseMessageItem(item);

                AppendEvent("response.output_item.added", new
                {
                    type = "response.output_item.added",
                    output_index = index,
                    item = new
                    {
                        type = "message",
                        id = messageId,
                        status = "in_progress",
                        role = "assistant",
                        content = Array.Empty<object>()
                    }
                });

                AppendEvent("response.content_part.added", new
                {
                    type = "response.content_part.added",
                    item_id = messageId,
                    output_index = index,
                    content_index = 0,
                    part = new
                    {
                        type = "output_text",
                        text = string.Empty,
                        annotations = Array.Empty<object>()
                    }
                });

                if (!string.IsNullOrEmpty(text))
                {
                    AppendEvent("response.output_text.delta", new
                    {
                        type = "response.output_text.delta",
                        item_id = messageId,
                        output_index = index,
                        content_index = 0,
                        delta = text
                    });
                }

                AppendEvent("response.output_text.done", new
                {
                    type = "response.output_text.done",
                    item_id = messageId,
                    output_index = index,
                    content_index = 0,
                    text
                });

                AppendEvent("response.output_item.done", new
                {
                    type = "response.output_item.done",
                    output_index = index,
                    item
                });
            }
            else
            {
                AppendEvent("response.output_item.added", new
                {
                    type = "response.output_item.added",
                    output_index = index,
                    item
                });

                AppendEvent("response.output_item.done", new
                {
                    type = "response.output_item.done",
                    output_index = index,
                    item
                });
            }
        }

        AppendEvent("response.completed", new
        {
            type = "response.completed",
            response = new
            {
                id = responseId,
                @object = "response",
                created,
                status = "completed",
                model = modelAlias,
                output = outputItems,
                usage = new
                {
                    input_tokens = promptTokens,
                    output_tokens = completionTokens,
                    total_tokens = promptTokens + completionTokens
                }
            }
        });

        return builder.ToString();
    }

    private static IResult CreateAnthropicError(
        int statusCode,
        string errorType,
        string message)
    {
        return Results.Json(
            new
            {
                type = "error",
                error = new
                {
                    type = errorType,
                    message
                },
                request_id = Guid.NewGuid().ToString("N")
            },
            JsonOptions,
            statusCode: statusCode);
    }

    private static string BuildAnthropicMessageSsePayload(
        string messageId,
        string modelAlias,
        IReadOnlyList<AnthropicContentBlock> contentBlocks,
        string stopReason,
        long promptTokens,
        long completionTokens)
    {
        var builder = new StringBuilder();

        void AppendEvent(string eventType, object payload)
        {
            builder.Append("event: ")
                .Append(eventType)
                .Append('\n')
                .Append("data: ")
                .Append(JsonSerializer.Serialize(payload, JsonOptions))
                .Append("\n\n");
        }

        AppendEvent("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = messageId,
                type = "message",
                role = "assistant",
                model = modelAlias,
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new
                {
                    input_tokens = promptTokens,
                    output_tokens = 0L
                }
            }
        });

        for (var index = 0; index < contentBlocks.Count; index++)
        {
            var block = contentBlocks[index];
            if (string.Equals(block.Type, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                var toolInput = block.Input ?? JsonSerializer.SerializeToElement(new { });
                AppendEvent("content_block_start", new
                {
                    type = "content_block_start",
                    index,
                    content_block = new
                    {
                        type = "tool_use",
                        id = block.ToolUseId,
                        name = block.ToolName,
                        input = new { }
                    }
                });

                var partialJson = toolInput.GetRawText();
                if (!string.IsNullOrWhiteSpace(partialJson))
                {
                    AppendEvent("content_block_delta", new
                    {
                        type = "content_block_delta",
                        index,
                        delta = new
                        {
                            type = "input_json_delta",
                            partial_json = partialJson
                        }
                    });
                }

                AppendEvent("content_block_stop", new
                {
                    type = "content_block_stop",
                    index
                });
                continue;
            }

            AppendEvent("content_block_start", new
            {
                type = "content_block_start",
                index,
                content_block = new
                {
                    type = "text",
                    text = string.Empty
                }
            });

            if (!string.IsNullOrEmpty(block.Text))
            {
                AppendEvent("content_block_delta", new
                {
                    type = "content_block_delta",
                    index,
                    delta = new
                    {
                        type = "text_delta",
                        text = block.Text
                    }
                });
            }

            AppendEvent("content_block_stop", new
            {
                type = "content_block_stop",
                index
            });
        }

        AppendEvent("message_delta", new
        {
            type = "message_delta",
            delta = new
            {
                stop_reason = stopReason,
                stop_sequence = (string?)null
            },
            usage = new
            {
                output_tokens = completionTokens
            }
        });

        AppendEvent("message_stop", new
        {
            type = "message_stop"
        });

        return builder.ToString();
    }

    private static long EstimateAnthropicInputTokens(JsonElement request)
    {
        try
        {
            var rawJson = request.GetRawText();
            var utf8Bytes = Encoding.UTF8.GetByteCount(rawJson);
            var estimated = (utf8Bytes + 3L) / 4L;
            return Math.Clamp(estimated, 1L, int.MaxValue);
        }
        catch
        {
            return 1;
        }
    }

    public sealed class OpenAiChatCompletionsRequest
    {
        public string? Model { get; set; }
        public JsonElement Messages { get; set; }
        public JsonElement Tools { get; set; }
        public bool? Stream { get; set; }

        [JsonPropertyName("tool_choice")]
        public JsonElement ToolChoice { get; set; }
    }

    public sealed class OpenAiResponsesRequest
    {
        public string? Model { get; set; }
        public JsonElement Input { get; set; }
        public JsonElement Tools { get; set; }
        public bool? Stream { get; set; }

        [JsonPropertyName("previous_response_id")]
        public string? PreviousResponseId { get; set; }

        [JsonPropertyName("tool_choice")]
        public JsonElement ToolChoice { get; set; }
    }

    public sealed class AnthropicMessagesRequest
    {
        public string? Model { get; set; }
        public JsonElement Messages { get; set; }
        public JsonElement System { get; set; }
        public JsonElement Tools { get; set; }
        public bool? Stream { get; set; }

        [JsonPropertyName("tool_choice")]
        public JsonElement ToolChoice { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }
    }

    public sealed class OpenAiEmbeddingsRequest
    {
        public string? Model { get; set; }
        public JsonElement Input { get; set; }
    }

    public sealed class OpenAiImageGenerationsRequest
    {
        public string? Model { get; set; }
        public string? Prompt { get; set; }
        public int? N { get; set; }
        public string? Size { get; set; }
        public string? ResponseFormat { get; set; }
    }

    public sealed class OpenAiAudioSpeechRequest
    {
        public string? Model { get; set; }
        public string? Input { get; set; }
        public string? Voice { get; set; }
        public string? ResponseFormat { get; set; }
    }
}
