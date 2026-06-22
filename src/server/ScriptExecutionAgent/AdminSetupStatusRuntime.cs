using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptExecutionAgent;

internal sealed record AdminSetupStatusResponse(
  string OverallStatus,
  string ScopeKey,
  Guid? ProjectId,
  Guid? GuideId,
  AdminSetupHealthStatus Health,
  AdminSetupRequirementsStatus? Requirements,
  AdminSetupInstallScriptsStatus? InstallScripts,
  AdminSetupAptStatus? Apt,
  AdminApplyJobStatus? ActiveApplyJob,
  AdminApplyJobStatus? LastApplyJob,
  IReadOnlyList<string> Errors);

internal sealed record AdminSetupHealthStatus(
  string Status,
  string AdminStateDir,
  string ScopeStateRoot);

internal sealed record AdminSetupRequirementsStatus(
  bool HasStagedContent,
  string? StagedHash,
  string? AppliedHash,
  bool PendingApply,
  int LineCount);

internal sealed record AdminSetupInstallScriptsStatus(
  int ScriptCount,
  string? StagedHash,
  string? AppliedHash,
  bool PendingApply,
  IReadOnlyList<AdminSetupInstallScriptStepStatus> Steps);

internal sealed record AdminSetupInstallScriptStepStatus(
  string Id,
  int Order,
  string? Name,
  string ScriptType,
  string LastStatus,
  int? ExitCode,
  string? LastError,
  DateTimeOffset? AppliedAtUtc);

internal sealed record AdminSetupAptStatus(
  bool HasStagedContent,
  string? StagedHash,
  string? AppliedHash,
  bool PendingApply,
  int PackageCount);

internal static class AdminSetupStatusRuntime
{
  internal static async Task<AdminSetupStatusResponse> BuildAsync(
    bool hasScope,
    ScriptExecutionScope? scope,
    ScriptExecutionScopeOptions scopeOptions,
    AdminApiOptions adminOptions,
    CancellationToken cancellationToken)
  {
    var scopeKey = hasScope
      ? $"project:{scope!.ProjectId:D}:guide:{scope.GuideScopeId:D}"
      : "global";

    var errors = new List<string>();
    var health = new AdminSetupHealthStatus(
      "OK",
      adminOptions.StateDirectoryPath,
      scopeOptions.StateRootPath);

    AdminSetupRequirementsStatus? requirementsStatus = null;
    AdminSetupInstallScriptsStatus? installScriptsStatus = null;
    AdminSetupAptStatus? aptStatus = null;

    if (hasScope)
    {
      var requirementsPath = File.Exists(scope!.RequirementsFilePath)
        ? scope.RequirementsFilePath
        : AdminStateRuntime.GetGlobalRequirementsPath(adminOptions);
      var requirementsText = File.Exists(requirementsPath)
        ? await File.ReadAllTextAsync(requirementsPath, cancellationToken)
        : string.Empty;
      var stagedRequirementsHash = ComputeSha256(requirementsText);
      var appliedState = AdminScopeAppliedStateRuntime.Read(scope);
      var lineCount = requirementsText.Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n')
        .Count(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'));

      requirementsStatus = new AdminSetupRequirementsStatus(
        HasStagedContent: !string.IsNullOrWhiteSpace(requirementsText),
        StagedHash: stagedRequirementsHash,
        AppliedHash: appliedState.RequirementsHash,
        PendingApply: !string.Equals(stagedRequirementsHash, appliedState.RequirementsHash, StringComparison.Ordinal),
        LineCount: lineCount);

      var installScriptsDocument = AdminInstallScriptsRuntime.ReadDocument(scope);
      var stagedInstallScriptsHash = AdminInstallScriptsRuntime.ComputeDocumentHash(installScriptsDocument);
      var stepResultsById = appliedState.InstallScriptStepResults.ToDictionary(result => result.Id, StringComparer.OrdinalIgnoreCase);
      var scriptsApplied = string.Equals(stagedInstallScriptsHash, appliedState.InstallScriptsHash, StringComparison.Ordinal);
      var steps = installScriptsDocument.Scripts
        .OrderBy(static step => step.Order)
        .Select(step =>
        {
          stepResultsById.TryGetValue(step.Id, out var lastResult);
          var lastStatus = lastResult?.Status
            ?? (scriptsApplied ? "applied" : "pending");
          return new AdminSetupInstallScriptStepStatus(
            step.Id,
            step.Order,
            step.Name,
            step.ScriptType,
            lastStatus,
            lastResult?.ExitCode,
            lastResult?.Error,
            lastResult?.AppliedAtUtc);
        })
        .ToArray();

      installScriptsStatus = new AdminSetupInstallScriptsStatus(
        installScriptsDocument.Scripts.Count,
        stagedInstallScriptsHash,
        appliedState.InstallScriptsHash,
        AdminInstallScriptsRuntime.NeedsApply(stagedInstallScriptsHash, appliedState.InstallScriptsHash, installScriptsDocument.Scripts.Count),
        steps);

      foreach (var failedStep in steps.Where(step => string.Equals(step.LastStatus, "failed", StringComparison.OrdinalIgnoreCase)))
      {
        errors.Add($"install script '{failedStep.Id}' failed: {failedStep.LastError ?? "unknown error"}");
      }
    }
    else
    {
      var aptPackagesPath = AdminStateRuntime.GetAptPackagesPath(adminOptions);
      var aptText = File.Exists(aptPackagesPath)
        ? await File.ReadAllTextAsync(aptPackagesPath, cancellationToken)
        : string.Empty;
      var stagedAptHash = ComputeSha256(aptText);
      var globalAppliedState = AdminStateRuntime.ReadGlobalAppliedState(adminOptions);
      globalAppliedState.TryGetValue("aptPackagesHash", out var appliedAptHash);
      var packageCount = aptText.Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n')
        .Count(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'));

      aptStatus = new AdminSetupAptStatus(
        HasStagedContent: !string.IsNullOrWhiteSpace(aptText),
        StagedHash: stagedAptHash,
        AppliedHash: appliedAptHash,
        PendingApply: !string.Equals(stagedAptHash, appliedAptHash, StringComparison.Ordinal),
        PackageCount: packageCount);
    }

    AdminApplyJobRuntime.TryGetActiveJobForScope(scopeKey, adminOptions, out var activeJob);
    AdminApplyJobRuntime.TryGetLatestJobForScope(scopeKey, adminOptions, out var lastJob);

    if (lastJob is { Status: "failed" } && !string.IsNullOrWhiteSpace(lastJob.Error))
    {
      errors.Add(lastJob.Error);
    }

    var overallStatus = ResolveOverallStatus(
      hasScope,
      requirementsStatus,
      installScriptsStatus,
      aptStatus,
      activeJob,
      lastJob,
      errors);

    return new AdminSetupStatusResponse(
      overallStatus,
      scopeKey,
      hasScope ? scope!.ProjectId : null,
      hasScope ? scope!.GuideScopeId : null,
      health,
      requirementsStatus,
      installScriptsStatus,
      aptStatus,
      activeJob,
      lastJob,
      errors);
  }

  private static string ResolveOverallStatus(
    bool hasScope,
    AdminSetupRequirementsStatus? requirements,
    AdminSetupInstallScriptsStatus? installScripts,
    AdminSetupAptStatus? apt,
    AdminApplyJobStatus? activeJob,
    AdminApplyJobStatus? lastJob,
    IReadOnlyList<string> errors)
  {
    if (activeJob is { Status: "queued" or "running" })
    {
      return "applying";
    }

    if (errors.Count > 0 || lastJob is { Status: "failed" })
    {
      return "failed";
    }

    var pending = hasScope
      ? (requirements?.PendingApply ?? false) || (installScripts?.PendingApply ?? false)
        || (installScripts?.Steps.Any(step => string.Equals(step.LastStatus, "pending", StringComparison.OrdinalIgnoreCase)) ?? false)
      : apt?.PendingApply ?? false;

    if (pending)
    {
      return "pending";
    }

    return "ready";
  }

  private static string ComputeSha256(string value)
  {
    var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes).ToLowerInvariant();
  }
}
