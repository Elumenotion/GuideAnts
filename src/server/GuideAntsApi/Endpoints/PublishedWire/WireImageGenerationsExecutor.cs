using AntRunner.ToolCalling;
using GuideAnts.Usage;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Endpoints.PublishedWire;

/// <summary>
/// Shared wire images.generations execution. Uses routed image generation and a plain
/// filesystem write — not the generate_image tool path.
/// </summary>
internal static class WireImageGenerationsExecutor
{
    internal sealed record Request(
        string Prompt,
        string Size,
        int N,
        InvocationContext RunContext);

    internal static async Task<IResult> ExecuteAsync(
        HttpContext httpContext,
        Request request,
        ServiceMode mode,
        IConfiguration configuration,
        INotebookImageService notebookImageService,
        INotebookFileSyncService? notebookFileSyncService,
        bool syncDatabaseAfterWrite,
        Func<UsageMetrics, long, long, Task> recordUsageAsync)
    {
        var imageBytes = await notebookImageService.GenerateImageBytesAsync(
            prompt: request.Prompt,
            size: request.Size,
            n: request.N,
            outputFormat: "png",
            context: request.RunContext,
            cancellationToken: httpContext.RequestAborted);

        if (imageBytes == null || imageBytes.Length == 0)
        {
            return OpenAiWireErrorResults.ProviderNotReady("Image generation did not return image data.");
        }

        var storageRoot = NotebookRunOutputWriter.ResolveStorageRoot(configuration);
        var fileName = NotebookRunOutputWriter.CreateWireFilename(request.RunContext, storageRoot, "png");
        await notebookImageService.WriteImageBytesToNotebookOutputAsync(
            imageBytes,
            fileName,
            request.RunContext,
            httpContext.RequestAborted);

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

        var base64 = Convert.ToBase64String(imageBytes);
        await recordUsageAsync(
            new UsageMetrics(ValueInput: request.Prompt.Length, ValueOther: imageBytes.Length),
            request.Prompt.Length,
            imageBytes.Length);

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
}
