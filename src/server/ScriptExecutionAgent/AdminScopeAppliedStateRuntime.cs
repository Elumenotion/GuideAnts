using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptExecutionAgent;

internal sealed record AdminScopeAppliedState(
  int Version,
  Guid ProjectId,
  Guid GuideId,
  string? RequirementsHash,
  string? RequirementsPath,
  string? InstallScriptsHash,
  IReadOnlyList<string> TopLevelPackages,
  IReadOnlyList<AdminInstallScriptStepResult> InstallScriptStepResults,
  DateTimeOffset? AppliedAtUtc);

internal static class AdminScopeAppliedStateRuntime
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
  };

  internal static AdminScopeAppliedState Read(ScriptExecutionScope scope)
  {
    if (!File.Exists(scope.AppliedStateFilePath))
    {
      return Empty(scope);
    }

    try
    {
      using var document = JsonDocument.Parse(File.ReadAllText(scope.AppliedStateFilePath));
      var root = document.RootElement;
      return new AdminScopeAppliedState(
        root.TryGetProperty("version", out var version) ? version.GetInt32() : 1,
        root.TryGetProperty("projectId", out var projectId) && Guid.TryParse(projectId.GetString(), out var parsedProjectId)
          ? parsedProjectId
          : scope.ProjectId,
        root.TryGetProperty("guideId", out var guideId) && Guid.TryParse(guideId.GetString(), out var parsedGuideId)
          ? parsedGuideId
          : scope.GuideScopeId,
        root.TryGetProperty("requirementsHash", out var requirementsHash) ? requirementsHash.GetString() : null,
        root.TryGetProperty("requirementsPath", out var requirementsPath) ? requirementsPath.GetString() : null,
        root.TryGetProperty("installScriptsHash", out var installScriptsHash) ? installScriptsHash.GetString() : null,
        ReadStringArray(root, "topLevelPackages"),
        ReadInstallScriptStepResults(root),
        ParseAppliedAt(root));
    }
    catch
    {
      return Empty(scope);
    }
  }

  internal static Task WriteAsync(
    ScriptExecutionScope scope,
    string? requirementsHash,
    string? requirementsPath,
    IReadOnlyCollection<string> desiredTopLevelPackages,
    string? installScriptsHash,
    IReadOnlyList<AdminInstallScriptStepResult> installScriptStepResults,
    CancellationToken cancellationToken)
  {
    var payload = new AdminScopeAppliedState(
      1,
      scope.ProjectId,
      scope.GuideScopeId,
      requirementsHash,
      requirementsPath,
      installScriptsHash,
      desiredTopLevelPackages.OrderBy(static package => package, StringComparer.Ordinal).ToArray(),
      installScriptStepResults,
      DateTimeOffset.UtcNow);

    var json = JsonSerializer.Serialize(payload, JsonOptions);
    return AtomicFile.WriteAllTextAsync(scope.AppliedStateFilePath, json, cancellationToken);
  }

  private static AdminScopeAppliedState Empty(ScriptExecutionScope scope) =>
    new(
      1,
      scope.ProjectId,
      scope.GuideScopeId,
      null,
      null,
      null,
      Array.Empty<string>(),
      Array.Empty<AdminInstallScriptStepResult>(),
      null);

  private static DateTimeOffset? ParseAppliedAt(JsonElement root)
  {
    if ((root.TryGetProperty("appliedAtUtc", out var appliedAt) || root.TryGetProperty("appliedAt", out appliedAt))
        && DateTimeOffset.TryParse(appliedAt.GetString(), out var parsedAppliedAt))
    {
      return parsedAppliedAt;
    }

    return null;
  }

  private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
  {
    if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
    {
      return Array.Empty<string>();
    }

    return array.EnumerateArray()
      .Select(element => element.GetString())
      .Where(value => !string.IsNullOrWhiteSpace(value))
      .Select(value => value!)
      .ToArray();
  }

  private static IReadOnlyList<AdminInstallScriptStepResult> ReadInstallScriptStepResults(JsonElement root)
  {
    if (!root.TryGetProperty("installScriptStepResults", out var array) || array.ValueKind != JsonValueKind.Array)
    {
      return Array.Empty<AdminInstallScriptStepResult>();
    }

    var results = new List<AdminInstallScriptStepResult>();
    foreach (var element in array.EnumerateArray())
    {
      if (!element.TryGetProperty("id", out var idElement))
      {
        continue;
      }

      var id = idElement.GetString();
      if (string.IsNullOrWhiteSpace(id))
      {
        continue;
      }

      results.Add(new AdminInstallScriptStepResult(
        id,
        element.TryGetProperty("order", out var orderElement) ? orderElement.GetInt32() : 0,
        element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
        element.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "unknown" : "unknown",
        element.TryGetProperty("exitCode", out var exitCodeElement) ? exitCodeElement.GetInt32() : 0,
        element.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null,
        element.TryGetProperty("appliedAtUtc", out var appliedAtElement)
          && DateTimeOffset.TryParse(appliedAtElement.GetString(), out var appliedAtUtc)
          ? appliedAtUtc
          : null));
    }

    return results;
  }
}
