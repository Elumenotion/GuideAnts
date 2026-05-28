using System.Text;
using GuideAntsApi.Services.Components;
using AntRunner.ToolCalling.Functions;
using GuideAnts.Usage;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;

namespace GuideAntsApi.Services;

public interface INotebookPodcastService
{
    Task<ScriptExecutionResult> GeneratePodcastAsync(
        string script,
        string filename,
        InvocationContext? context = null);
}

public class NotebookPodcastService : INotebookPodcastService
{
    private readonly ISpeechSynthesisService _speechSynthesis;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotebookPodcastService> _logger;
    private readonly IServiceProvider _serviceProvider;
    
    public NotebookPodcastService(
        ISpeechSynthesisService speechSynthesis,
        IConfiguration configuration,
        ILogger<NotebookPodcastService> logger,
        IServiceProvider serviceProvider)
    {
        _speechSynthesis = speechSynthesis;
        _configuration = configuration;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Generates a podcast using Azure Speech Service and saves it to the notebook's output directory.
    /// This method is called by the tool calling system with injected notebook context.
    /// </summary>
    /// <param name="script">The SSML or plain text to convert to speech</param>
    /// <param name="filename">The filename for the generated audio file</param>
    /// <param name="context">The invocation context containing project, notebook, user, and conversation IDs</param>
    /// <returns>Podcast generation result</returns>
    public async Task<ScriptExecutionResult> GeneratePodcastAsync(
        string script,
        string filename,
        InvocationContext? context = null)
    {
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();
        Exception? executionException = null;

        _logger.LogInformation("GeneratePodcast invoked. Project={ProjectId}, Notebook={NotebookId}, Script length={ScriptLength}", context?.ProjectId, context?.NotebookId, script?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(script))
        {
            var errorMsg = "Script cannot be null or empty.";
            _logger?.LogError(errorMsg);
            stdErrBuffer.AppendLine(errorMsg);
            return new ScriptExecutionResult
            {
                StandardOutput = stdOutBuffer.ToString(),
                StandardError = stdErrBuffer.ToString(),
            };
        }


        // Validate filename
        if (string.IsNullOrWhiteSpace(filename))
        {
            var errorMsg = "Filename is required.";
            _logger?.LogError(errorMsg);
            stdErrBuffer.AppendLine(errorMsg);
            return new ScriptExecutionResult
            {
                StandardOutput = stdOutBuffer.ToString(),
                StandardError = stdErrBuffer.ToString(),
            };
        }

        // Sanitize filename (prevent path traversal and ensure .wav extension)
        filename = Path.GetFileName(filename);
        if (!filename.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            filename += ".wav";
        }

        // Validate project and notebook IDs
        if (context?.ProjectId == null || context?.NotebookId == null)
        {
            var errorMsg = "Project ID and Notebook ID are required.";
            _logger?.LogError(errorMsg + " ProjectId='{ProjectId}', NotebookId='{NotebookId}'", context?.ProjectId, context?.NotebookId);
            stdErrBuffer.AppendLine(errorMsg);
            return new ScriptExecutionResult
            {
                StandardOutput = stdOutBuffer.ToString(),
                StandardError = stdErrBuffer.ToString(),
            };
        }

        // Determine notebook output directory based on FileStorage path configuration
        var storageRoot = Environment.GetEnvironmentVariable("FileStorage__Path") ?? Environment.GetEnvironmentVariable("FILESTORAGE__PATH");
        if (string.IsNullOrWhiteSpace(storageRoot) && _serviceProvider != null)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                storageRoot = cfg["FileStorage:Path"];
            }
            catch { /* ignore – will fall back to default */ }
        }
        if (storageRoot == null) throw new InvalidOperationException("FileStorage:Path is not configured");
        var notebookDirectory = NotebookPathHelper.GetLocalWorkingDirectory(context, storageRoot);

            _logger?.LogInformation("Will generate podcast and save to directory: {NotebookDirectory}", notebookDirectory);

            // Track the created file for NewFiles property
            string? createdFileCwdPath = null;

            try
            {
            // Ensure output directory exists
            Directory.CreateDirectory(notebookDirectory);

            // Build full file path
            var filePath = Path.Combine(notebookDirectory, filename);

            // Generate podcast via Azure Speech Service
            var synthResult = await _speechSynthesis.SynthesizeToWavAsync(script, filePath);

            if (synthResult.Success)
            {
                _logger?.LogInformation("Podcast saved to: {FilePath}", filePath);
                stdOutBuffer.AppendLine($"Podcast generated successfully: {filename}");

                // Calculate relative path for the generated file
                var relativePath = Path.Combine(NotebookPathHelper.GetRelativeRunFolder(context), filename).Replace("\\", "/");

                // Reconcile DB with disk before returning so the new file is queryable.
                if (context?.NotebookId != null)
                {
                    try
                    {
                        using var scope = _serviceProvider?.CreateScope();
                        if (scope == null) return new ScriptExecutionResult
                        {
                            StandardOutput = string.Empty,
                            StandardError = "Service provider not available"
                        };
                        var syncService = scope.ServiceProvider.GetRequiredService<INotebookFileSyncService>();
                        await syncService.SyncNotebookAsync(context.NotebookId);
                        _logger?.LogInformation("Database synchronized for notebook {NotebookId} after podcast generation", context.NotebookId);

                        // Record TTS usage and storage
                        var recorder = scope.ServiceProvider.GetRequiredService<GuideAnts.Usage.IUsageRecorder>();
                        if (recorder != null)
                        {
                            // Log warning if AssistantId is missing (should not happen in normal flow)
                            if (!context.AssistantId.HasValue)
                            {
                                _logger?.LogWarning("GeneratePodcast: AssistantId is null in context, usage recording may be incomplete");
                            }
                            
                            await recorder.RecordTtsAsync(
                                projectId: context.ProjectId,
                                notebookId: context.NotebookId,
                                notebookFileId: null,
                                characterCount: script.Length,
                                service: synthResult.ProviderId ?? "SpeechSynthesis.Unknown",
                                conversationId: context.ConversationId,
                                metadataJson: null,
                                assistantId: context.AssistantId,
                                agentInvocationId: context.CurrentInvocationId,
                                notebookConversationMessageId: context.NotebookConversationMessageIdForUsage);

                            var fileInfo = new FileInfo(filePath);
                            await recorder.RecordAsync(
                                projectId: context.ProjectId,
                                notebookId: context.NotebookId,
                                category: UsageCategory.StorageSystemGenerated,
                                service: "Storage",
                                operation: "system",
                                metrics: new UsageMetrics(ValueOther: fileInfo.Exists ? fileInfo.Length : 0),
                                assistantId: context.AssistantId,
                                agentInvocationId: context.CurrentInvocationId,
                                metadataJson: null);
                        }
                    }
                    catch (Exception syncEx)
                    {
                        _logger?.LogError(syncEx, "Failed to sync database after podcast generation");
                        stdErrBuffer.AppendLine($"Warning: Podcast generated but failed to sync database: {syncEx.Message}");
                    }
                }

                // Convert to CWD-relative path for the assistant
                if (context?.NotebookId != null && context?.ProjectId != null)
                {
                    createdFileCwdPath = NotebookFileChangeReporter.ToCwdRelativePath(relativePath, context.IsPublished, context.RunId);
                }
            }
            else
            {
                var errMsg = string.IsNullOrWhiteSpace(synthResult.ErrorMessage)
                    ? "Podcast generation failed – unknown error."
                    : $"Podcast generation failed – {synthResult.ErrorMessage}";
                stdErrBuffer.AppendLine(errMsg);
                _logger?.LogWarning(errMsg);
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"Error generating podcast: {ex.Message}";
            _logger?.LogError(ex, "Podcast generation failed. Project={ProjectId}, Notebook={NotebookId}, Script length={ScriptLength}", context?.ProjectId, context?.NotebookId, script?.Length ?? 0);
            stdErrBuffer.AppendLine(errorMsg);
            executionException = ex;
        }

        if (stdOutBuffer.Length == 0 && stdErrBuffer.Length == 0)
        {
            stdOutBuffer.AppendLine("The operation completed successfully");
        }

        // Clean up output formatting
        var cleanedOutput = stdOutBuffer.ToString()
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .TrimEnd();

        var cleanedError = stdErrBuffer.ToString()
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .TrimEnd();

        var podcastGenerationResult = new ScriptExecutionResult
        {
            StandardOutput = cleanedOutput,
            StandardError = cleanedError,
            NewFiles = createdFileCwdPath != null ? new List<string> { createdFileCwdPath } : null
        };

        // Emit final buffers to logs (truncated to 4k chars)
        if (!string.IsNullOrWhiteSpace(cleanedOutput))
        {
            _logger?.LogInformation("Final StdOut (truncated): {Out}", cleanedOutput.Length > 4096 ? cleanedOutput.Substring(0, 4096) + "…" : cleanedOutput);
        }
        if (!string.IsNullOrWhiteSpace(cleanedError))
        {
            _logger?.LogWarning("Final StdErr (truncated): {Err}", cleanedError.Length > 4096 ? cleanedError.Substring(0, 4096) + "…" : cleanedError);
        }

        return podcastGenerationResult;
    }

    // Static service provider for tool calling system compatibility
    private static IServiceProvider? _staticServiceProvider;
    
    /// <summary>
    /// Initializes the static service provider for tool calling system compatibility
    /// </summary>
    public static void InitializeServiceProvider(IServiceProvider serviceProvider)
    {
        _staticServiceProvider = serviceProvider;
    }

    /// <summary>
    /// Static wrapper method for tool calling system compatibility
    /// </summary>
    [Tool(
        OperationId = "generate_podcast",
        Summary = "Generate a WAV podcast from SSML"
    )]
    [RequiresNotebookContext]
    public static async Task<ScriptExecutionResult> GeneratePodcast(
        [Parameter(Description = "SSML (Speech Synthesis Markup Language) For multi-voice podcasts, use Azure Speech Service SSML with <speak> as root element containing multiple <voice> elements. Each <voice> element should specify a voice name via the 'name' attribute (e.g., 'en-US-BrianMultilingualNeural', 'en-US-EmmaMultilingualNeural', 'en-US-AvaMultilingualNeural', 'en-US-AndrewMultilingualNeural'). Wrap each speaker's dialogue in separate <voice> elements to alternate between speakers. Example: <speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'><voice name='en-US-AvaMultilingualNeural'>Hello, this is Ava speaking.</voice><voice name='en-US-BrianMultilingualNeural'>And this is Brian responding.</voice></speak>. For advanced multi-talker conversations, use <mstts:dialog> with speaker IDs like 'emma' and 'andrew' for contextual flow preservation.")] string script,
        [Parameter(Description = "Desired filename for the generated WAV file (must end with .wav).")] string filename,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
    {
        if (_staticServiceProvider == null)
        {
            throw new InvalidOperationException("Service provider not initialized. Call InitializeServiceProvider during application startup.");
        }

        using var scope = _staticServiceProvider.CreateScope();
        var podcastService = scope.ServiceProvider.GetRequiredService<INotebookPodcastService>();
        
        return await podcastService.GeneratePodcastAsync(script, filename, context);
    }
}
