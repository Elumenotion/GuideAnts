using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Functions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Scheduling;

public sealed record ProjectScheduledJobExecutionResult(
    bool Succeeded,
    string? ErrorMessage,
    string? StandardOutput,
    string? StandardError,
    Guid? CreatedConversationId,
    int? ExitCode);

public interface IProjectScheduledJobExecutor
{
    Task<ProjectScheduledJobExecutionResult> ExecuteAsync(
        ProjectScheduledJob job,
        CancellationToken cancellationToken);
}

public sealed class ProjectScheduledJobExecutor : IProjectScheduledJobExecutor
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IConversationService _conversationService;
    private readonly INotebookDockerScriptService _dockerScriptService;
    private readonly IStoragePathResolver _pathResolver;
    private readonly ILogger<ProjectScheduledJobExecutor> _logger;

    public ProjectScheduledJobExecutor(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IConversationService conversationService,
        INotebookDockerScriptService dockerScriptService,
        IStoragePathResolver pathResolver,
        ILogger<ProjectScheduledJobExecutor> logger)
    {
        _dbFactory = dbFactory;
        _conversationService = conversationService;
        _dockerScriptService = dockerScriptService;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    public Task<ProjectScheduledJobExecutionResult> ExecuteAsync(
        ProjectScheduledJob job,
        CancellationToken cancellationToken) =>
        job.JobType switch
        {
            ProjectScheduledJobType.NewConversation => ExecuteNewConversationAsync(job, cancellationToken),
            ProjectScheduledJobType.RunPythonScript => ExecuteRunPythonScriptAsync(job, cancellationToken),
            _ => Task.FromResult(new ProjectScheduledJobExecutionResult(
                false,
                $"Unsupported job type '{job.JobType}'.",
                null,
                null,
                null,
                null))
        };

    private async Task<ProjectScheduledJobExecutionResult> ExecuteNewConversationAsync(
        ProjectScheduledJob job,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.Prompt))
        {
            return Fail("Prompt is required for new conversation jobs.");
        }

        if (string.IsNullOrWhiteSpace(job.AssistantName))
        {
            return Fail("Assistant is required for new conversation jobs.");
        }

        var title = ResolveConversationTitle(job.ConversationTitle);
        var conversation = await _conversationService.CreateConversationAsync(job.NotebookId, title);
        var request = new SendMessageRequest
        {
            Instructions = job.Prompt.Trim(),
            AssistantName = job.AssistantName.Trim()
        };

        string? errorMessage = null;
        var pendingClientTool = false;

        try
        {
            await foreach (var ev in _conversationService.SendMessageStreamToConversationAsUserAsync(
                               conversation.Id,
                               request,
                               job.CreatedByUserId,
                               cancellationToken))
            {
                switch (ev.EventType)
                {
                    case StreamingEventTypes.PendingClientTool:
                    case StreamingEventTypes.ExternalToolCall:
                        pendingClientTool = true;
                        break;
                    case StreamingEventTypes.Error:
                        errorMessage = ev.Payload;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled new conversation job {JobId} failed during streaming", job.Id);
            return Fail(ex.Message, createdConversationId: conversation.Id);
        }

        if (pendingClientTool)
        {
            return Fail(
                "The selected assistant requires client-side tools, which are not available for scheduled jobs.",
                createdConversationId: conversation.Id);
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return Fail(errorMessage, createdConversationId: conversation.Id);
        }

        return new ProjectScheduledJobExecutionResult(
            true,
            null,
            $"Created conversation '{title}' and submitted prompt.",
            null,
            conversation.Id,
            null);
    }

    private async Task<ProjectScheduledJobExecutionResult> ExecuteRunPythonScriptAsync(
        ProjectScheduledJob job,
        CancellationToken cancellationToken)
    {
        if (job.ScriptNotebookFileId is not Guid fileId)
        {
            return Fail("A Python script file is required for run script jobs.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var notebookFile = await db.NotebookFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.NotebookId == job.NotebookId, cancellationToken);

        if (notebookFile == null)
        {
            return Fail("The selected Python script file was not found in the target notebook.");
        }

        if (!notebookFile.RelativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("The selected file must be a Python (.py) script.");
        }

        var notebookRoot = _pathResolver.GetNotebookRootPath(job.ProjectId, job.NotebookId);
        var scriptPath = Path.Combine(
            notebookRoot,
            notebookFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(scriptPath))
        {
            return Fail($"Script file '{notebookFile.RelativePath}' was not found on disk.");
        }

        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        var context = new InvocationContext(
            job.ProjectId,
            job.NotebookId,
            Guid.Empty);

        ScriptExecutionResult result;
        try
        {
            result = await _dockerScriptService.ExecuteDockerScriptAsync(
                script,
                "guideants-ai",
                ScriptType.Python,
                context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled Python job {JobId} failed during script execution", job.Id);
            return Fail(ex.Message);
        }

        var stdout = ScheduledJobOutputTruncator.Truncate(result.StandardOutput);
        var stderr = ScheduledJobOutputTruncator.Truncate(result.StandardError);

        if (result.ExitCode is not int exitCode)
        {
            return new ProjectScheduledJobExecutionResult(
                false,
                "Script execution did not report an exit code.",
                stdout,
                stderr,
                null,
                null);
        }

        var succeeded = exitCode == 0;
        return new ProjectScheduledJobExecutionResult(
            succeeded,
            succeeded ? null : $"Script exited with code {exitCode}.",
            stdout,
            stderr,
            null,
            exitCode);
    }

    private static string ResolveConversationTitle(string? template)
    {
        var value = string.IsNullOrWhiteSpace(template)
            ? "Scheduled conversation"
            : template.Trim();
        return value.Replace("{timestamp}", DateTime.UtcNow.ToString("u"), StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectScheduledJobExecutionResult Fail(
        string message,
        Guid? createdConversationId = null) =>
        new(false, message, null, null, createdConversationId, null);
}
