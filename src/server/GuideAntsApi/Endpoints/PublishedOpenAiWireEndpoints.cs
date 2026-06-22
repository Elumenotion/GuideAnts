using System.Text;
using System.Text.Json;
using AntRunner.ToolCalling;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Mvc;

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
        string Text,
        long PromptTokens,
        long CompletionTokens,
        bool PendingClientTool,
        string? ErrorPayload);

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
        [FromServices] IPublishedConversationService publishedConversationService)
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

        if (request.Stream == true)
        {
            return OpenAiWireErrorResults.UnsupportedFeature("Streaming is not supported on this endpoint.", "stream");
        }

        var context = resolution.Context!;
        var modelAlias = ResolveModelAliasOrError(context, AliasKeys.Guide, request.Model);
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        var instructions = BuildInstructionsFromChatMessages(request.Messages);
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "At least one textual message is required.",
                type: "invalid_request_error",
                code: "invalid_messages",
                param: "messages");
        }

        try
        {
            var conversation = await ExecuteConversationAsync(
                publishedConversationService,
                context,
                instructions,
                httpContext.RequestAborted);

            if (conversation.PendingClientTool)
            {
                return OpenAiWireErrorResults.UnsupportedFeature(
                    "This request triggered client-side tool execution, which is not supported on non-streaming wire APIs.");
            }

            if (!string.IsNullOrWhiteSpace(conversation.ErrorPayload))
            {
                return OpenAiWireErrorResults.ProviderNotReady("Provider execution failed for this request.");
            }

            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var usage = BuildOpenAiUsage(conversation.PromptTokens, conversation.CompletionTokens);
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
                        message = new
                        {
                            role = "assistant",
                            content = conversation.Text
                        },
                        finish_reason = "stop"
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
        [FromServices] IPublishedConversationService publishedConversationService)
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

        if (request.Stream == true)
        {
            return OpenAiWireErrorResults.UnsupportedFeature("Streaming is not supported on this endpoint.", "stream");
        }

        var context = resolution.Context!;
        var modelAlias = ResolveModelAliasOrError(context, AliasKeys.Guide, request.Model);
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        var instructions = BuildInstructionsFromResponsesInput(request.Input);
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return OpenAiWireErrorResults.Create(
                StatusCodes.Status400BadRequest,
                "The input field must contain text.",
                type: "invalid_request_error",
                code: "invalid_input",
                param: "input");
        }

        try
        {
            var conversation = await ExecuteConversationAsync(
                publishedConversationService,
                context,
                instructions,
                httpContext.RequestAborted);

            if (conversation.PendingClientTool)
            {
                return OpenAiWireErrorResults.UnsupportedFeature(
                    "This request triggered client-side tool execution, which is not supported on non-streaming wire APIs.");
            }

            if (!string.IsNullOrWhiteSpace(conversation.ErrorPayload))
            {
                return OpenAiWireErrorResults.ProviderNotReady("Provider execution failed for this request.");
            }

            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var promptTokens = conversation.PromptTokens;
            var completionTokens = conversation.CompletionTokens;
            return Results.Json(new
            {
                id = $"resp_{Guid.NewGuid():N}",
                @object = "response",
                created,
                status = "completed",
                model = modelAlias.Alias,
                output = new[]
                {
                    new
                    {
                        id = $"msg_{Guid.NewGuid():N}",
                        type = "message",
                        role = "assistant",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = conversation.Text,
                                annotations = Array.Empty<object>()
                            }
                        }
                    }
                },
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
        PublishedApiExecutionContext context,
        string instructions,
        CancellationToken ct)
    {
        var conversation = await publishedConversationService.CreateConversationAsync(
            context.NotebookId,
            $"wire-{DateTime.UtcNow:yyyyMMddHHmmss}");
        var request = new SendMessageRequest
        {
            Instructions = instructions
        };

        var assistantText = new StringBuilder();
        long promptTokens = 0;
        long completionTokens = 0;
        string? errorPayload = null;
        var pendingClientTool = false;

        await foreach (var ev in publishedConversationService.SendMessageStreamAsync(
                           conversation.Id,
                           request,
                           context.PubId.ToString(),
                           context.ExternalUserIdentity,
                           context.InternalUserId,
                           ct))
        {
            if (string.Equals(ev.EventType, StreamingEventTypes.PendingClientTool, StringComparison.Ordinal))
            {
                pendingClientTool = true;
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

        return new WireConversationResult(
            Text: assistantText.ToString(),
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            PendingClientTool: pendingClientTool,
            ErrorPayload: errorPayload);
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

        if (flags.ChatCompletions != false || flags.Responses != false)
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

    public sealed class OpenAiChatCompletionsRequest
    {
        public string? Model { get; set; }
        public JsonElement Messages { get; set; }
        public bool? Stream { get; set; }
    }

    public sealed class OpenAiResponsesRequest
    {
        public string? Model { get; set; }
        public JsonElement Input { get; set; }
        public bool? Stream { get; set; }
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
