using AntRunner.ToolCalling;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.Endpoints.PublishedWire;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Services.SandboxWireApi;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Endpoints;

public static class SandboxWireMediaHandlers
{
    public static async Task<IResult> PostEmbeddingsAsync(
        HttpContext httpContext,
        OpenAiEmbeddingsRequest request,
        ISandboxWireExecutionContextResolver executionContextResolver,
        IEmbeddingService embeddingService,
        IServiceModeResolver serviceModeResolver,
        IPublishedWireUsageRecorder wireUsageRecorder)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            endpointName: "embeddings",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(
            context,
            WireModelAliasResolver.AliasKeys.Embeddings,
            request.Model);
        if (modelAlias.ErrorResult != null)
        {
            return modelAlias.ErrorResult;
        }

        var inputs = WireClientRequestParser.ParseEmbeddingsInput(request.Input);
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

    public static async Task<IResult> PostImageGenerationsAsync(
        HttpContext httpContext,
        OpenAiImageGenerationsRequest request,
        ISandboxWireExecutionContextResolver executionContextResolver,
        INotebookImageService notebookImageService,
        IServiceModeResolver serviceModeResolver,
        IConfiguration configuration,
        IPublishedWireUsageRecorder wireUsageRecorder)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            endpointName: "images.generations",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(
            context,
            WireModelAliasResolver.AliasKeys.Image,
            request.Model);
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
                ConversationId: context.AttributionConversationId ?? Guid.NewGuid())
            {
                AssistantId = context.OwnerAssistantId
            };

            return await WireImageGenerationsExecutor.ExecuteAsync(
                httpContext,
                new WireImageGenerationsExecutor.Request(
                    Prompt: request.Prompt,
                    Size: string.IsNullOrWhiteSpace(request.Size) ? "1024x1024" : request.Size!,
                    N: request.N.GetValueOrDefault(1),
                    RunContext: runContext),
                mode,
                configuration,
                notebookImageService,
                notebookFileSyncService: null,
                syncDatabaseAfterWrite: false,
                recordUsageAsync: async (metrics, inputCount, outputCount) => await wireUsageRecorder.RecordAsync(
                    context: context,
                    category: UsageCategory.ImageGeneration,
                    service: mode.ProviderSection,
                    operation: "images.generations",
                    metrics: metrics,
                    endpoint: "images.generations",
                    alias: modelAlias.Alias,
                    providerModel: mode.ModelId,
                    providerServiceMode: mode.ModeId,
                    requestBytes: httpContext.Request.ContentLength,
                    inputCount: inputCount,
                    outputCount: outputCount,
                    ct: httpContext.RequestAborted));
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
        HttpRequest request,
        ISandboxWireExecutionContextResolver executionContextResolver,
        ISpeechTranscriptionService transcriptionService,
        IServiceModeResolver serviceModeResolver,
        IPublishedWireUsageRecorder wireUsageRecorder)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
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
        var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(
            context,
            WireModelAliasResolver.AliasKeys.Transcription,
            form["model"].ToString());
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

            await wireUsageRecorder.RecordTranscriptionAsync(
                context: context,
                service: mode.ProviderSection,
                operation: "audio.transcriptions",
                endpoint: "audio.transcriptions",
                durationSeconds: result.DurationSeconds,
                transcriptLength: result.Text.Length,
                alias: modelAlias.Alias,
                providerModel: mode.ModelId,
                providerServiceMode: mode.ModeId,
                requestBytes: file.Length,
                ct: httpContext.RequestAborted);

            return Results.Json(new { text = result.Text }, WireJson.SerializationOptions);
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
        OpenAiAudioSpeechRequest request,
        ISandboxWireExecutionContextResolver executionContextResolver,
        ISpeechSynthesisService speechSynthesisService,
        IServiceModeResolver serviceModeResolver,
        IConfiguration configuration,
        IPublishedWireUsageRecorder wireUsageRecorder)
    {
        var resolution = await executionContextResolver.ResolveAsync(
            httpContext,
            endpointName: "audio.speech",
            ct: httpContext.RequestAborted);
        if (!resolution.Success)
        {
            return resolution.ErrorResult!;
        }

        var context = resolution.Context!;
        var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(
            context,
            WireModelAliasResolver.AliasKeys.Speech,
            request.Model);
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

        try
        {
            var mode = await serviceModeResolver.ResolveAsync(RoutedServiceNames.SpeechSynthesis, modeId: null, httpContext.RequestAborted);
            var runContext = new InvocationContext(
                ProjectId: context.ProjectId,
                NotebookId: context.NotebookId,
                ConversationId: context.AttributionConversationId ?? Guid.NewGuid())
            {
                AssistantId = context.OwnerAssistantId
            };

            return await WireAudioSpeechExecutor.ExecuteAsync(
                httpContext,
                new WireAudioSpeechExecutor.Request(request.Input, runContext),
                mode,
                speechSynthesisService,
                configuration,
                notebookFileSyncService: null,
                syncDatabaseAfterWrite: false,
                recordUsageAsync: async (synthResult, characterCount, durationSeconds) => await wireUsageRecorder.RecordSpeechAsync(
                    context: context,
                    service: synthResult.ProviderId ?? mode.ProviderSection,
                    operation: "audio.speech",
                    endpoint: "audio.speech",
                    characterCount: characterCount,
                    durationSeconds: durationSeconds,
                    alias: modelAlias.Alias,
                    providerModel: mode.ModelId,
                    providerServiceMode: mode.ModeId,
                    requestBytes: httpContext.Request.ContentLength,
                    ct: httpContext.RequestAborted));
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
