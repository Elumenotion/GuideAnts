using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptExecutionAgent;

internal sealed record AdminApplyJobAccepted(
    string JobId,
    string Status,
    string PollPath);

internal sealed record AdminApplyJobStatus(
    string JobId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string ScopeKey,
    Guid? ProjectId,
    Guid? GuideId,
    AdminApplyResult? Result,
    string? Error);

internal static class AdminApplyJobRuntime
{
    private static readonly ConcurrentDictionary<string, AdminApplyJobRecord> JobsById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ActiveJobByScopeKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static TimeSpan ApplyTimeout { get; } = ResolveApplyTimeout();
    internal static TimeSpan PreflightTimeout { get; } = ResolvePreflightTimeout();

    internal static async Task<AdminApplyJobAccepted> StartApplyAsync(
        bool hasScope,
        ScriptExecutionScope? scope,
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var scopeKey = BuildScopeKey(hasScope, scope);
        if (ActiveJobByScopeKey.TryGetValue(scopeKey, out var existingJobId)
            && JobsById.TryGetValue(existingJobId, out var existingJob)
            && existingJob.IsActive)
        {
            return ToAccepted(existingJob);
        }

        using var preflightCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        preflightCts.CancelAfter(PreflightTimeout);
        if (hasScope)
        {
            await ScriptExecutionScopeRuntime.PreflightScopeRequirementsAsync(
                scope!,
                scopeOptions,
                adminOptions,
                logger,
                preflightCts.Token);
        }
        else
        {
            await AdminStateRuntime.PreflightGlobalApplyAsync(
                scopeOptions,
                adminOptions,
                logger,
                preflightCts.Token);
        }

        var job = new AdminApplyJobRecord(
            Guid.NewGuid().ToString("N"),
            scopeKey,
            hasScope,
            scope?.ProjectId,
            scope?.GuideScopeId);
        JobsById[job.JobId] = job;
        ActiveJobByScopeKey[scopeKey] = job.JobId;
        await PersistJobAsync(job, adminOptions, cancellationToken);

        _ = Task.Run(() => RunJobAsync(job, hasScope, scope, scopeOptions, adminOptions, logger));

        return ToAccepted(job);
    }

    internal static bool TryGetActiveJobForScope(
        string scopeKey,
        AdminApiOptions adminOptions,
        out AdminApplyJobStatus? status)
    {
        status = null;
        if (!ActiveJobByScopeKey.TryGetValue(scopeKey, out var jobId)
            || !JobsById.TryGetValue(jobId, out var job)
            || !job.IsActive)
        {
            return false;
        }

        status = job.ToStatus();
        return true;
    }

    internal static bool TryGetLatestJobForScope(
        string scopeKey,
        AdminApiOptions adminOptions,
        out AdminApplyJobStatus? status)
    {
        status = null;
        AdminApplyJobRecord? latest = null;
        foreach (var job in JobsById.Values.Where(candidate => string.Equals(candidate.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)))
        {
            if (latest is null || job.CompletedAtUtc > latest.CompletedAtUtc)
            {
                latest = job;
            }
        }

        if (latest is null)
        {
            latest = LoadLatestPersistedJobForScope(scopeKey, adminOptions);
            if (latest is not null)
            {
                JobsById[latest.JobId] = latest;
            }
        }

        if (latest is null || latest.IsActive)
        {
            return false;
        }

        status = latest.ToStatus();
        return true;
    }

    private static AdminApplyJobRecord? LoadLatestPersistedJobForScope(string scopeKey, AdminApiOptions adminOptions)
    {
        var jobsDirectory = GetJobsDirectory(adminOptions);
        if (!Directory.Exists(jobsDirectory))
        {
            return null;
        }

        AdminApplyJobRecord? latest = null;
        foreach (var jobFile in Directory.EnumerateFiles(jobsDirectory, "*.json"))
        {
            var jobId = Path.GetFileNameWithoutExtension(jobFile);
            var job = TryLoadPersistedJob(jobId, adminOptions);
            if (job is null || !string.Equals(job.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (latest is null || (job.CompletedAtUtc ?? job.CreatedAtUtc) > (latest.CompletedAtUtc ?? latest.CreatedAtUtc))
            {
                latest = job;
            }
        }

        return latest;
    }

    internal static bool TryGetStatus(string jobId, AdminApiOptions adminOptions, out AdminApplyJobStatus? status)
    {
        status = null;
        if (!JobsById.TryGetValue(jobId, out var job))
        {
            job = TryLoadPersistedJob(jobId, adminOptions);
            if (job is null)
            {
                return false;
            }

            JobsById[job.JobId] = job;
        }

        status = job.ToStatus();
        return true;
    }

    private static async Task RunJobAsync(
        AdminApplyJobRecord job,
        bool hasScope,
        ScriptExecutionScope? scope,
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        ILogger logger)
    {
        job.MarkRunning();
        await PersistJobAsync(job, adminOptions, CancellationToken.None);

        using var cts = new CancellationTokenSource(ApplyTimeout);
        try
        {
            AdminApplyResult result;
            if (hasScope)
            {
                result = await ScriptExecutionScopeRuntime.ApplyScopeRequirementsAsync(
                    scope!,
                    scopeOptions,
                    adminOptions,
                    logger,
                    cts.Token);
            }
            else
            {
                var aptResult = await AdminStateRuntime.ApplyGlobalAptPackagesAsync(adminOptions, logger, cts.Token);
                var scopeResult = await AdminStateRuntime.ApplyAllKnownScopesAsync(
                    scopeOptions,
                    adminOptions,
                    logger,
                    cts.Token);
                result = scopeResult with
                {
                    Apt = new AdminApplyResultDetails(
                        aptResult.Status,
                        aptResult.ScopesApplied,
                        aptResult.ScopesSkipped,
                        aptResult.Errors)
                };
            }

            job.MarkSucceeded(result);
            logger.LogInformation(
                "Admin apply job succeeded. jobId={JobId} scopeKey={ScopeKey} status={Status}",
                job.JobId,
                job.ScopeKey,
                result.Status);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var message = $"Apply timed out after {ApplyTimeout.TotalMinutes:0} minutes.";
            job.MarkFailed(message);
            logger.LogWarning(
                "Admin apply job timed out. jobId={JobId} scopeKey={ScopeKey}",
                job.JobId,
                job.ScopeKey);
        }
        catch (InvalidOperationException ex)
        {
            job.MarkFailed(ex.Message);
            logger.LogWarning(
                ex,
                "Admin apply job rejected. jobId={JobId} scopeKey={ScopeKey}",
                job.JobId,
                job.ScopeKey);
        }
        catch (Exception ex)
        {
            job.MarkFailed(ex.Message);
            logger.LogError(
                ex,
                "Admin apply job failed. jobId={JobId} scopeKey={ScopeKey}",
                job.JobId,
                job.ScopeKey);
        }
        finally
        {
            if (ActiveJobByScopeKey.TryGetValue(job.ScopeKey, out var activeJobId)
                && string.Equals(activeJobId, job.JobId, StringComparison.OrdinalIgnoreCase))
            {
                ActiveJobByScopeKey.TryRemove(job.ScopeKey, out _);
            }

            await PersistJobAsync(job, adminOptions, CancellationToken.None);
        }
    }

    private static AdminApplyJobAccepted ToAccepted(AdminApplyJobRecord job) =>
        new(job.JobId, job.Status, $"/admin/apply/jobs/{job.JobId}");

    private static string BuildScopeKey(bool hasScope, ScriptExecutionScope? scope) =>
        hasScope
            ? $"project:{scope!.ProjectId:D}:guide:{scope.GuideScopeId:D}"
            : "global";

    private static string GetJobsDirectory(AdminApiOptions adminOptions) =>
        Path.Combine(adminOptions.StateDirectoryPath, "apply-jobs");

    private static string GetJobFilePath(AdminApiOptions adminOptions, string jobId) =>
        Path.Combine(GetJobsDirectory(adminOptions), $"{jobId}.json");

    private static async Task PersistJobAsync(
        AdminApplyJobRecord job,
        AdminApiOptions adminOptions,
        CancellationToken cancellationToken)
    {
        var jobsDirectory = GetJobsDirectory(adminOptions);
        Directory.CreateDirectory(jobsDirectory);
        var payload = JsonSerializer.Serialize(job.ToPersisted(), JsonOptions);
        await AtomicFile.WriteAllTextAsync(GetJobFilePath(adminOptions, job.JobId), payload, cancellationToken);
    }

    private static AdminApplyJobRecord? TryLoadPersistedJob(string jobId, AdminApiOptions adminOptions)
    {
        var jobFilePath = GetJobFilePath(adminOptions, jobId);
        if (!File.Exists(jobFilePath))
        {
            return null;
        }

        try
        {
            var payload = File.ReadAllText(jobFilePath);
            var persisted = JsonSerializer.Deserialize<PersistedAdminApplyJob>(payload, JsonOptions);
            return persisted is null ? null : AdminApplyJobRecord.FromPersisted(persisted);
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan ResolveApplyTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_APPLY_TIMEOUT_MINUTES");
        if (int.TryParse(raw, out var minutes) && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return TimeSpan.FromMinutes(60);
    }

    private static TimeSpan ResolvePreflightTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_APPLY_PREFLIGHT_TIMEOUT_SECONDS");
        if (int.TryParse(raw, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(120);
    }

    private sealed class AdminApplyJobRecord
    {
        public string JobId { get; }
        public string ScopeKey { get; }
        public bool HasScope { get; }
        public Guid? ProjectId { get; }
        public Guid? GuideId { get; }
        public string Status { get; private set; }
        public DateTimeOffset CreatedAtUtc { get; }
        public DateTimeOffset? StartedAtUtc { get; private set; }
        public DateTimeOffset? CompletedAtUtc { get; private set; }
        public AdminApplyResult? Result { get; private set; }
        public string? Error { get; private set; }

        public bool IsActive => Status is "queued" or "running";

        public AdminApplyJobRecord(
            string jobId,
            string scopeKey,
            bool hasScope,
            Guid? projectId,
            Guid? guideId)
        {
            JobId = jobId;
            ScopeKey = scopeKey;
            HasScope = hasScope;
            ProjectId = projectId;
            GuideId = guideId;
            Status = "queued";
            CreatedAtUtc = DateTimeOffset.UtcNow;
        }

        private AdminApplyJobRecord(PersistedAdminApplyJob persisted)
        {
            JobId = persisted.JobId;
            ScopeKey = persisted.ScopeKey;
            HasScope = persisted.HasScope;
            ProjectId = persisted.ProjectId;
            GuideId = persisted.GuideId;
            Status = persisted.Status;
            CreatedAtUtc = persisted.CreatedAtUtc;
            StartedAtUtc = persisted.StartedAtUtc;
            CompletedAtUtc = persisted.CompletedAtUtc;
            Result = persisted.Result;
            Error = persisted.Error;
        }

        public static AdminApplyJobRecord FromPersisted(PersistedAdminApplyJob persisted) => new(persisted);

        public void MarkRunning()
        {
            Status = "running";
            StartedAtUtc = DateTimeOffset.UtcNow;
        }

        public void MarkSucceeded(AdminApplyResult result)
        {
            Status = "succeeded";
            Result = result;
            CompletedAtUtc = DateTimeOffset.UtcNow;
        }

        public void MarkFailed(string error)
        {
            Status = "failed";
            Error = error;
            CompletedAtUtc = DateTimeOffset.UtcNow;
        }

        public AdminApplyJobStatus ToStatus() =>
            new(
                JobId,
                Status,
                CreatedAtUtc,
                StartedAtUtc,
                CompletedAtUtc,
                ScopeKey,
                ProjectId,
                GuideId,
                Result,
                Error);

        public PersistedAdminApplyJob ToPersisted() =>
            new(
                JobId,
                ScopeKey,
                HasScope,
                ProjectId,
                GuideId,
                Status,
                CreatedAtUtc,
                StartedAtUtc,
                CompletedAtUtc,
                Result,
                Error);
    }

    private sealed record PersistedAdminApplyJob(
        string JobId,
        string ScopeKey,
        bool HasScope,
        Guid? ProjectId,
        Guid? GuideId,
        string Status,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        AdminApplyResult? Result,
        string? Error);
}
