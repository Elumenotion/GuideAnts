using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Scheduling;

public interface IProjectScheduledJobService
{
    Task<IReadOnlyList<ProjectScheduledJobSummaryDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<ProjectScheduledJobDetailDto?> GetAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);

    Task<ProjectScheduledJobDetailDto> CreateAsync(
        Guid projectId,
        CreateProjectScheduledJobRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default);

    Task<ProjectScheduledJobDetailDto> UpdateAsync(
        Guid projectId,
        Guid jobId,
        UpdateProjectScheduledJobRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);

    Task EnqueueManualRunAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default);

    Task<PagedProjectScheduledJobRunsDto> ListRunsAsync(
        Guid projectId,
        Guid jobId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<ProjectScheduledJobRunDetailDto?> GetRunAsync(
        Guid projectId,
        Guid jobId,
        Guid runId,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectScheduledJobService : IProjectScheduledJobService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IScheduleBuilderService _scheduleBuilder;
    private readonly ICronScheduleService _cronScheduleService;
    private readonly IJobQueueService _jobQueueService;

    public ProjectScheduledJobService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IScheduleBuilderService scheduleBuilder,
        ICronScheduleService cronScheduleService,
        IJobQueueService jobQueueService)
    {
        _dbFactory = dbFactory;
        _scheduleBuilder = scheduleBuilder;
        _cronScheduleService = cronScheduleService;
        _jobQueueService = jobQueueService;
    }

    public async Task<IReadOnlyList<ProjectScheduledJobSummaryDto>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ProjectScheduledJobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId)
            .OrderBy(j => j.Name)
            .Select(j => new ScheduledJobSummaryRow(
                j.Id,
                j.Name,
                j.JobType,
                j.NotebookId,
                j.Notebook.Title,
                j.IsEnabled,
                j.CronExpression,
                j.TimeZoneId,
                j.NextRunUtc,
                j.LastRunUtc,
                j.LastRunStatus,
                j.CreatedUtc,
                j.UpdatedUtc))
            .ToListAsync(cancellationToken);

        return rows.Select(MapSummary).ToList();
    }

    public async Task<ProjectScheduledJobDetailDto?> GetAsync(
        Guid projectId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await LoadJobDetailRowAsync(db, projectId, jobId, cancellationToken);
        return row == null ? null : MapDetail(row);
    }

    public async Task<ProjectScheduledJobDetailDto> CreateAsync(
        Guid projectId,
        CreateProjectScheduledJobRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        await EnsureProjectExistsAsync(db, projectId, cancellationToken);
        var jobType = ParseJobType(request.JobType);
        var cron = BuildAndValidateSchedule(request.Schedule, request.TimeZoneId);
        await ValidateJobPayloadAsync(db, projectId, jobType, request.NotebookId, request.Prompt, request.AssistantName, request.ScriptNotebookFileId, cancellationToken);
        await EnsureUniqueNameAsync(db, projectId, request.Name.Trim(), excludeJobId: null, cancellationToken);

        var now = DateTime.UtcNow;
        var job = new ProjectScheduledJob
        {
            ProjectId = projectId,
            Name = request.Name.Trim(),
            JobType = jobType,
            NotebookId = request.NotebookId,
            IsEnabled = request.IsEnabled,
            CronExpression = cron,
            TimeZoneId = request.TimeZoneId.Trim(),
            ConversationTitle = request.ConversationTitle?.Trim(),
            Prompt = request.Prompt?.Trim(),
            AssistantName = request.AssistantName?.Trim(),
            ScriptNotebookFileId = request.ScriptNotebookFileId,
            ExposeSandboxWireApi = request.ExposeSandboxWireApi,
            WireTargetAssistantId = request.WireTargetAssistantId,
            WireAttributionConversationTitle = request.WireAttributionConversationTitle?.Trim(),
            WireCreateAttributionConversationPerRun = request.WireCreateAttributionConversationPerRun,
            WireDailyLimitUsd = request.WireDailyLimitUsd,
            WireMonthlyLimitUsd = request.WireMonthlyLimitUsd,
            CreatedByUserId = createdByUserId,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        job.NextRunUtc = request.IsEnabled
            ? _cronScheduleService.GetNextOccurrenceUtc(job.CronExpression, job.TimeZoneId, now)
            : null;

        db.ProjectScheduledJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        var loaded = await LoadJobDetailRowAsync(db, projectId, job.Id, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load created scheduled job.");
        return MapDetail(loaded);
    }

    public async Task<ProjectScheduledJobDetailDto> UpdateAsync(
        Guid projectId,
        Guid jobId,
        UpdateProjectScheduledJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var job = await db.ProjectScheduledJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Scheduled job {jobId} was not found.");

        var jobType = ParseJobType(request.JobType);
        var cron = BuildAndValidateSchedule(request.Schedule, request.TimeZoneId);
        await ValidateJobPayloadAsync(db, projectId, jobType, request.NotebookId, request.Prompt, request.AssistantName, request.ScriptNotebookFileId, cancellationToken);
        await EnsureUniqueNameAsync(db, projectId, request.Name.Trim(), excludeJobId: jobId, cancellationToken);

        job.Name = request.Name.Trim();
        job.JobType = jobType;
        job.NotebookId = request.NotebookId;
        job.IsEnabled = request.IsEnabled;
        job.CronExpression = cron;
        job.TimeZoneId = request.TimeZoneId.Trim();
        job.ConversationTitle = request.ConversationTitle?.Trim();
        job.Prompt = request.Prompt?.Trim();
        job.AssistantName = request.AssistantName?.Trim();
        job.ScriptNotebookFileId = request.ScriptNotebookFileId;
        job.ExposeSandboxWireApi = request.ExposeSandboxWireApi;
        job.WireTargetAssistantId = request.WireTargetAssistantId;
        job.WireAttributionConversationTitle = request.WireAttributionConversationTitle?.Trim();
        job.WireCreateAttributionConversationPerRun = request.WireCreateAttributionConversationPerRun;
        job.WireDailyLimitUsd = request.WireDailyLimitUsd;
        job.WireMonthlyLimitUsd = request.WireMonthlyLimitUsd;
        job.UpdatedUtc = DateTime.UtcNow;
        job.NextRunUtc = request.IsEnabled
            ? _cronScheduleService.GetNextOccurrenceUtc(job.CronExpression, job.TimeZoneId, DateTime.UtcNow)
            : null;

        await db.SaveChangesAsync(cancellationToken);

        var loaded = await LoadJobDetailRowAsync(db, projectId, job.Id, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load updated scheduled job.");
        return MapDetail(loaded);
    }

    public async Task DeleteAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.ProjectScheduledJobs
            .FirstOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException($"Scheduled job {jobId} was not found.");

        db.ProjectScheduledJobs.Remove(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task EnqueueManualRunAsync(Guid projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.ProjectScheduledJobs.AsNoTracking()
            .Where(j => j.Id == jobId && j.ProjectId == projectId)
            .Select(j => new
            {
                HasRunningRun = j.Runs.Any(r => r.Status == ProjectScheduledJobRunStatus.Running)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (state == null)
        {
            throw new KeyNotFoundException($"Scheduled job {jobId} was not found.");
        }

        if (state.HasRunningRun)
        {
            throw new InvalidOperationException("A run is already in progress for this scheduled job.");
        }

        if (await ProjectScheduledJobInFlightGuard.HasInFlightQueueItemAsync(db, jobId, cancellationToken))
        {
            throw new InvalidOperationException("A run is already queued for this scheduled job.");
        }

        await _jobQueueService.EnqueueAsync(
            jobType: ProjectScheduledJobExecutionJob.JobType,
            payload: new ProjectScheduledJobExecutionJob(jobId, ProjectScheduledJobTrigger.Manual.ToString()),
            ct: cancellationToken);
    }

    public async Task<PagedProjectScheduledJobRunsDto> ListRunsAsync(
        Guid projectId,
        Guid jobId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.ProjectScheduledJobRuns.AsNoTracking()
            .Where(r => r.ScheduledJobId == jobId && r.ScheduledJob.ProjectId == projectId);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(r => r.StartedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ProjectScheduledJobRunSummaryDto(
                r.Id,
                r.TriggeredBy.ToString(),
                AsUtc(r.StartedUtc),
                AsUtc(r.CompletedUtc),
                r.Status.ToString(),
                r.ErrorMessage,
                r.CreatedConversationId,
                r.ExitCode))
            .ToListAsync(cancellationToken);

        return new PagedProjectScheduledJobRunsDto(items, total, page, pageSize);
    }

    public async Task<ProjectScheduledJobRunDetailDto?> GetRunAsync(
        Guid projectId,
        Guid jobId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var run = await db.ProjectScheduledJobRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ScheduledJobId == jobId && r.ScheduledJob.ProjectId == projectId,
                cancellationToken);

        return run == null
            ? null
            : new ProjectScheduledJobRunDetailDto(
                run.Id,
                run.TriggeredBy.ToString(),
                AsUtc(run.StartedUtc),
                AsUtc(run.CompletedUtc),
                run.Status.ToString(),
                run.ErrorMessage,
                run.StandardOutput,
                run.StandardError,
                run.CreatedConversationId,
                run.ExitCode);
    }

    private static async Task<ScheduledJobDetailRow?> LoadJobDetailRowAsync(
        ApplicationDbContext db,
        Guid projectId,
        Guid jobId,
        CancellationToken cancellationToken) =>
        await db.ProjectScheduledJobs.AsNoTracking()
            .Where(j => j.Id == jobId && j.ProjectId == projectId)
            .Select(j => new ScheduledJobDetailRow(
                j.Id,
                j.Name,
                j.JobType,
                j.NotebookId,
                j.Notebook.Title,
                j.IsEnabled,
                j.CronExpression,
                j.TimeZoneId,
                j.ConversationTitle,
                j.Prompt,
                j.AssistantName,
                j.ScriptNotebookFileId,
                j.ScriptNotebookFile != null ? j.ScriptNotebookFile.RelativePath : null,
                j.ExposeSandboxWireApi,
                j.WireTargetAssistantId,
                j.WireAttributionConversationTitle,
                j.WireCreateAttributionConversationPerRun,
                j.WireDailyLimitUsd,
                j.WireMonthlyLimitUsd,
                j.NextRunUtc,
                j.LastRunUtc,
                j.LastRunStatus,
                j.CreatedByUserId,
                j.CreatedUtc,
                j.UpdatedUtc))
            .FirstOrDefaultAsync(cancellationToken);

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? AsUtc(value.Value) : null;

    private sealed record ScheduledJobSummaryRow(
        Guid Id,
        string Name,
        ProjectScheduledJobType JobType,
        Guid NotebookId,
        string NotebookTitle,
        bool IsEnabled,
        string CronExpression,
        string TimeZoneId,
        DateTime? NextRunUtc,
        DateTime? LastRunUtc,
        ProjectScheduledJobLastRunStatus? LastRunStatus,
        DateTime CreatedUtc,
        DateTime UpdatedUtc);

    private sealed record ScheduledJobDetailRow(
        Guid Id,
        string Name,
        ProjectScheduledJobType JobType,
        Guid NotebookId,
        string NotebookTitle,
        bool IsEnabled,
        string CronExpression,
        string TimeZoneId,
        string? ConversationTitle,
        string? Prompt,
        string? AssistantName,
        Guid? ScriptNotebookFileId,
        string? ScriptRelativePath,
        bool ExposeSandboxWireApi,
        Guid? WireTargetAssistantId,
        string? WireAttributionConversationTitle,
        bool WireCreateAttributionConversationPerRun,
        decimal? WireDailyLimitUsd,
        decimal? WireMonthlyLimitUsd,
        DateTime? NextRunUtc,
        DateTime? LastRunUtc,
        ProjectScheduledJobLastRunStatus? LastRunStatus,
        Guid CreatedByUserId,
        DateTime CreatedUtc,
        DateTime UpdatedUtc);

    private ProjectScheduledJobSummaryDto MapSummary(ScheduledJobSummaryRow row)
    {
        var friendly = _scheduleBuilder.ParseToFriendly(row.CronExpression);
        return new ProjectScheduledJobSummaryDto(
            row.Id,
            row.Name,
            row.JobType.ToString(),
            row.NotebookId,
            row.NotebookTitle,
            row.IsEnabled,
            row.CronExpression,
            row.TimeZoneId,
            _cronScheduleService.GetHumanReadableSummary(row.CronExpression, row.TimeZoneId, friendly),
            friendly,
            AsUtc(row.NextRunUtc),
            AsUtc(row.LastRunUtc),
            row.LastRunStatus?.ToString(),
            AsUtc(row.CreatedUtc),
            AsUtc(row.UpdatedUtc));
    }

    private ProjectScheduledJobDetailDto MapDetail(ScheduledJobDetailRow row)
    {
        var friendly = _scheduleBuilder.ParseToFriendly(row.CronExpression);
        return new ProjectScheduledJobDetailDto(
            row.Id,
            row.Name,
            row.JobType.ToString(),
            row.NotebookId,
            row.NotebookTitle,
            row.IsEnabled,
            row.CronExpression,
            row.TimeZoneId,
            _cronScheduleService.GetHumanReadableSummary(row.CronExpression, row.TimeZoneId, friendly),
            friendly,
            row.ConversationTitle,
            row.Prompt,
            row.AssistantName,
            row.ScriptNotebookFileId,
            row.ScriptRelativePath,
            row.ExposeSandboxWireApi,
            row.WireTargetAssistantId,
            row.WireAttributionConversationTitle,
            row.WireCreateAttributionConversationPerRun,
            row.WireDailyLimitUsd,
            row.WireMonthlyLimitUsd,
            AsUtc(row.NextRunUtc),
            AsUtc(row.LastRunUtc),
            row.LastRunStatus?.ToString(),
            row.CreatedByUserId,
            AsUtc(row.CreatedUtc),
            AsUtc(row.UpdatedUtc));
    }

    private string BuildAndValidateSchedule(FriendlyScheduleDto schedule, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ArgumentException("Timezone is required.", nameof(timeZoneId));
        }

        _ = CronScheduleService.ResolveTimeZone(timeZoneId);

        var built = _scheduleBuilder.BuildCron(schedule);
        if (!built.IsValid || string.IsNullOrWhiteSpace(built.CronExpression))
        {
            throw new ArgumentException(built.ErrorMessage ?? "Invalid schedule.");
        }

        if (!_cronScheduleService.TryValidate(built.CronExpression, out var cronError))
        {
            throw new ArgumentException(cronError ?? "Invalid cron expression.");
        }

        return built.CronExpression;
    }

    private static ProjectScheduledJobType ParseJobType(string jobType)
    {
        if (!Enum.TryParse<ProjectScheduledJobType>(jobType, ignoreCase: true, out var parsed))
        {
            throw new ArgumentException("Job type must be 'NewConversation' or 'RunPythonScript'.", nameof(jobType));
        }

        return parsed;
    }

    private static async Task EnsureProjectExistsAsync(ApplicationDbContext db, Guid projectId, CancellationToken ct)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct))
        {
            throw new KeyNotFoundException($"Project {projectId} was not found.");
        }
    }

    private static async Task EnsureUniqueNameAsync(
        ApplicationDbContext db,
        Guid projectId,
        string name,
        Guid? excludeJobId,
        CancellationToken ct)
    {
        var exists = await db.ProjectScheduledJobs.AsNoTracking()
            .AnyAsync(j => j.ProjectId == projectId && j.Name == name && (excludeJobId == null || j.Id != excludeJobId), ct);
        if (exists)
        {
            throw new InvalidOperationException($"A scheduled job named '{name}' already exists in this project.");
        }
    }

    private static async Task ValidateJobPayloadAsync(
        ApplicationDbContext db,
        Guid projectId,
        ProjectScheduledJobType jobType,
        Guid notebookId,
        string? prompt,
        string? assistantName,
        Guid? scriptNotebookFileId,
        CancellationToken ct)
    {
        var notebookExists = await db.Notebooks.AsNoTracking()
            .AnyAsync(n => n.Id == notebookId && n.ProjectId == projectId, ct);
        if (!notebookExists)
        {
            throw new KeyNotFoundException($"Notebook {notebookId} was not found in project {projectId}.");
        }

        switch (jobType)
        {
            case ProjectScheduledJobType.NewConversation:
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    throw new ArgumentException("Prompt is required for new conversation jobs.");
                }

                if (string.IsNullOrWhiteSpace(assistantName))
                {
                    throw new ArgumentException("Assistant is required for new conversation jobs.");
                }

                break;

            case ProjectScheduledJobType.RunPythonScript:
                if (scriptNotebookFileId is not Guid fileId)
                {
                    throw new ArgumentException("Script file is required for run Python script jobs.");
                }

                var scriptFile = await db.NotebookFiles.AsNoTracking()
                    .FirstOrDefaultAsync(f => f.Id == fileId && f.NotebookId == notebookId, ct);
                if (scriptFile == null)
                {
                    throw new KeyNotFoundException("Script file was not found in the selected notebook.");
                }

                if (!scriptFile.RelativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Script file must have a .py extension.");
                }

                break;
        }
    }
}
