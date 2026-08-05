using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptExecutionAgent;

internal sealed record ScopeRuntimeAppliedState(
  int Version,
  Guid ProjectId,
  Guid GuideId,
  string? RequirementsHash,
  string? InstallScriptsHash,
  DateTimeOffset? AppliedAtUtc);

internal static class ScopeRuntimeAppliedStateRuntime
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
  };

  internal static ScopeRuntimeAppliedState Read(ScriptExecutionScope scope)
  {
    if (!File.Exists(scope.RuntimeAppliedStateFilePath))
    {
      return Empty(scope);
    }

    try
    {
      using var document = JsonDocument.Parse(File.ReadAllText(scope.RuntimeAppliedStateFilePath));
      var root = document.RootElement;
      return new ScopeRuntimeAppliedState(
        root.TryGetProperty("version", out var version) ? version.GetInt32() : 1,
        root.TryGetProperty("projectId", out var projectId) && Guid.TryParse(projectId.GetString(), out var parsedProjectId)
          ? parsedProjectId
          : scope.ProjectId,
        root.TryGetProperty("guideId", out var guideId) && Guid.TryParse(guideId.GetString(), out var parsedGuideId)
          ? parsedGuideId
          : scope.GuideScopeId,
        root.TryGetProperty("requirementsHash", out var requirementsHash) ? requirementsHash.GetString() : null,
        root.TryGetProperty("installScriptsHash", out var installScriptsHash) ? installScriptsHash.GetString() : null,
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
    string? installScriptsHash,
    CancellationToken cancellationToken)
  {
    var payload = new ScopeRuntimeAppliedState(
      1,
      scope.ProjectId,
      scope.GuideScopeId,
      requirementsHash,
      installScriptsHash,
      DateTimeOffset.UtcNow);

    var json = JsonSerializer.Serialize(payload, JsonOptions);
    return AtomicFile.WriteAllTextAsync(scope.RuntimeAppliedStateFilePath, json, cancellationToken);
  }

  private static ScopeRuntimeAppliedState Empty(ScriptExecutionScope scope) =>
    new(
      1,
      scope.ProjectId,
      scope.GuideScopeId,
      null,
      null,
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
}
