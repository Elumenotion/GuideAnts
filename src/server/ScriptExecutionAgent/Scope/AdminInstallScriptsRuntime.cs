using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CliWrap;
using CliWrap.Buffered;

namespace ScriptExecutionAgent;

internal sealed record AdminInstallScriptsDocument(
  int Version,
  [property: JsonPropertyName("scripts")] IReadOnlyList<AdminInstallScriptStep> Scripts);

internal sealed record AdminInstallScriptStep(
  string Id,
  int Order,
  string? Name,
  string ScriptType,
  string Script);

internal sealed record AdminInstallScriptStepResult(
  string Id,
  int Order,
  string? Name,
  string Status,
  int ExitCode,
  string? Error,
  DateTimeOffset? AppliedAtUtc);

internal sealed record AdminInstallScriptsApplyDetails(
  string Status,
  int StepsApplied,
  int StepsSkipped,
  int StepsFailed,
  IReadOnlyList<AdminInstallScriptStepResult> StepResults);

internal static class AdminInstallScriptsRuntime
{
  private const int MaxInstallScripts = 32;
  private const int MaxInstallScriptChars = 256 * 1024;
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
  };

  internal static string GetInstallScriptsPath(ScriptExecutionScope scope) =>
    Path.Combine(scope.ScopeRootPath, "install-scripts.json");

  internal static AdminInstallScriptsDocument ReadDocument(ScriptExecutionScope scope)
  {
    var path = GetInstallScriptsPath(scope);
    if (!File.Exists(path))
    {
      return new AdminInstallScriptsDocument(1, Array.Empty<AdminInstallScriptStep>());
    }

    try
    {
      var document = JsonSerializer.Deserialize<AdminInstallScriptsDocument>(File.ReadAllText(path), JsonOptions);
      return document ?? new AdminInstallScriptsDocument(1, Array.Empty<AdminInstallScriptStep>());
    }
    catch
    {
      return new AdminInstallScriptsDocument(1, Array.Empty<AdminInstallScriptStep>());
    }
  }

  internal static Task<AdminInstallScriptsDocument> ParseAndValidateSubmitAsync(
    string rawJson,
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    AdminInstallScriptsDocument document;
    try
    {
      document = JsonSerializer.Deserialize<AdminInstallScriptsDocument>(rawJson, JsonOptions)
        ?? new AdminInstallScriptsDocument(1, Array.Empty<AdminInstallScriptStep>());
    }
    catch (JsonException ex)
    {
      throw new InvalidOperationException($"install-scripts.json is not valid JSON: {ex.Message}");
    }

    if (document.Version != 1)
    {
      throw new InvalidOperationException("install-scripts.json version must be 1.");
    }

    if (document.Scripts.Count > MaxInstallScripts)
    {
      throw new InvalidOperationException($"install-scripts.json supports at most {MaxInstallScripts} scripts.");
    }

    var normalized = new List<AdminInstallScriptStep>(document.Scripts.Count);
    var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < document.Scripts.Count; index++)
    {
      var step = document.Scripts[index];
      var script = step.Script ?? string.Empty;
      if (string.IsNullOrWhiteSpace(script))
      {
        throw new InvalidOperationException($"install script at index {index} is empty.");
      }

      if (script.Length > MaxInstallScriptChars)
      {
        throw new InvalidOperationException($"install script at index {index} exceeds the maximum size of {MaxInstallScriptChars} characters.");
      }

      if (!TryParseScriptType(step.ScriptType, out var scriptType))
      {
        throw new InvalidOperationException($"install script at index {index} has invalid scriptType '{step.ScriptType}'. Use Python or Bash.");
      }

      var id = string.IsNullOrWhiteSpace(step.Id) ? Guid.NewGuid().ToString("N") : step.Id.Trim();
      if (!seenIds.Add(id))
      {
        throw new InvalidOperationException($"install script id '{id}' is duplicated.");
      }

      normalized.Add(new AdminInstallScriptStep(
        id,
        index + 1,
        string.IsNullOrWhiteSpace(step.Name) ? null : step.Name.Trim(),
        scriptType.ToString(),
        script));
    }

    return Task.FromResult(new AdminInstallScriptsDocument(1, normalized));
  }

  internal static async Task PersistDocumentAsync(
    ScriptExecutionScope scope,
    AdminInstallScriptsDocument document,
    CancellationToken cancellationToken)
  {
    Directory.CreateDirectory(scope.ScopeRootPath);
    var payload = JsonSerializer.Serialize(document, JsonOptions);
    await AtomicFile.WriteAllTextAsync(GetInstallScriptsPath(scope), payload, cancellationToken);
  }

  internal static string ComputeDocumentHash(AdminInstallScriptsDocument document) =>
    ComputeSha256(JsonSerializer.Serialize(document, JsonOptions));

  internal static async Task PreflightSyntaxAsync(
    ScriptExecutionScope scope,
    ScriptExecutionScopeOptions scopeOptions,
    AdminInstallScriptsDocument document,
    ILogger logger,
    CancellationToken cancellationToken)
  {
    if (document.Scripts.Count == 0)
    {
      return;
    }

    await ScriptExecutionScopeRuntime.EnsurePythonVenvAsync(scope, scopeOptions, logger, cancellationToken);
    var workDirectory = Path.Combine(scope.ScopeRootPath, "install-scripts-work");
    Directory.CreateDirectory(workDirectory);

  foreach (var step in document.Scripts.OrderBy(static script => script.Order))
    {
      if (!TryParseScriptType(step.ScriptType, out var scriptType))
      {
        throw new InvalidOperationException($"install script '{step.Id}' has invalid scriptType.");
      }

      var extension = scriptType == ScriptType.Python ? ".py" : ".sh";
      var scriptPath = Path.Combine(workDirectory, $"{step.Order:000}-{step.Id}{extension}");
      await File.WriteAllTextAsync(scriptPath, step.Script, cancellationToken);

      if (scriptType == ScriptType.Python)
      {
        var pythonExecutable = File.Exists(scope.PythonExecutablePath) ? scope.PythonExecutablePath : "python3";
        var result = await Cli.Wrap(pythonExecutable)
          .WithArguments(args => args.Add("-m").Add("py_compile").Add(scriptPath))
          .WithValidation(CommandResultValidation.None)
          .ExecuteBufferedAsync(cancellationToken);
        if (result.ExitCode != 0)
        {
          throw new InvalidOperationException(
            $"install script '{step.Id}' failed Python syntax validation: {TrimCliOutput(result)}");
        }
      }
      else
      {
        var result = await Cli.Wrap("bash")
          .WithArguments(args => args.Add("-n").Add(scriptPath))
          .WithValidation(CommandResultValidation.None)
          .ExecuteBufferedAsync(cancellationToken);
        if (result.ExitCode != 0)
        {
          throw new InvalidOperationException(
            $"install script '{step.Id}' failed Bash syntax validation: {TrimCliOutput(result)}");
        }
      }
    }
  }

  internal static bool NeedsApply(string? stagedHash, string? appliedHash, int scriptCount) =>
    scriptCount > 0 && !string.Equals(stagedHash, appliedHash, StringComparison.Ordinal);

  private static bool TryParseScriptType(string? raw, out ScriptType scriptType)
  {
    scriptType = default;
    if (string.IsNullOrWhiteSpace(raw))
    {
      return false;
    }

    return Enum.TryParse(raw.Trim(), ignoreCase: true, out scriptType)
      && scriptType is ScriptType.Python or ScriptType.Bash;
  }

  private static string TrimCliOutput(BufferedCommandResult result)
  {
    var stderr = result.StandardError?.Trim();
    if (!string.IsNullOrWhiteSpace(stderr))
    {
      return stderr;
    }

    return result.StandardOutput?.Trim() ?? string.Empty;
  }

  private static string ComputeSha256(string value)
  {
    var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes).ToLowerInvariant();
  }
}
