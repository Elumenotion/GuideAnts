using AntRunner.ToolCalling;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints.PublishedWire;

public static class PublishedWireMediaHandler
{
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
    var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(context, WireModelAliasResolver.AliasKeys.Embeddings, request.Model);
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
    var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(context, WireModelAliasResolver.AliasKeys.Image, request.Model);
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
    var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(context, WireModelAliasResolver.AliasKeys.Transcription, form["model"].ToString());
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
    var modelAlias = WireModelAliasResolver.ResolveModelAliasOrError(context, WireModelAliasResolver.AliasKeys.Speech, request.Model);
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
}
