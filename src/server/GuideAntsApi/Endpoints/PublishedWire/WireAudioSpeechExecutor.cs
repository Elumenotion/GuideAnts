using AntRunner.ToolCalling;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Endpoints.PublishedWire;

/// <summary>
/// Shared wire audio.speech execution. Synthesizes into notebook output for quality control
/// without using a tool-call lifecycle.
/// </summary>
internal static class WireAudioSpeechExecutor
{
    internal sealed record Request(string Input, InvocationContext RunContext);

    internal static async Task<IResult> ExecuteAsync(
        HttpContext httpContext,
        Request request,
        ServiceMode mode,
        ISpeechSynthesisService speechSynthesisService,
        IConfiguration configuration,
        INotebookFileSyncService? notebookFileSyncService,
        bool syncDatabaseAfterWrite,
        Func<ISpeechSynthesisService.SpeechSynthesisResult, long, long, Task> recordUsageAsync)
    {
        var storageRoot = NotebookRunOutputWriter.ResolveStorageRoot(configuration);
        var fileName = NotebookRunOutputWriter.CreateWireFilename(request.RunContext, storageRoot, "wav");
        var outputPath = NotebookRunOutputWriter.BuildOutputFilePath(
            request.RunContext,
            storageRoot,
            fileName);

        var result = await speechSynthesisService.SynthesizeToWavAsync(
            request.Input,
            outputPath,
            httpContext.RequestAborted);

        if (!result.Success)
        {
            return OpenAiWireErrorResults.ProviderNotReady(result.ErrorMessage ?? "Speech synthesis failed.");
        }

        if (!File.Exists(outputPath))
        {
            return OpenAiWireErrorResults.ProviderNotReady("Speech synthesis did not produce an output file.");
        }

        if (syncDatabaseAfterWrite)
        {
            if (notebookFileSyncService == null)
            {
                throw new InvalidOperationException(
                    "Notebook file sync is required when syncDatabaseAfterWrite is enabled.");
            }

            await notebookFileSyncService.QueueNotebookSyncAsync(
                request.RunContext.NotebookId,
                httpContext.RequestAborted);
        }

        var bytes = await File.ReadAllBytesAsync(outputPath, httpContext.RequestAborted);
        await recordUsageAsync(result, request.Input.Length, result.DurationSeconds);

        return Results.File(bytes, "audio/wav");
    }
}
