using Microsoft.Extensions.Logging;
using GuideAnts.Logging;
namespace ScriptExecutionAgent;

/// <summary>
/// Request, environment, guide-scope, and timeout validation for script execution.
/// </summary>
internal static class ExecutionRequestValidator
{
    internal static ValidationResult ValidateExecutionRequest(ScriptExecutionRequest request, ScriptExecutionConfig config)
    {
        if (string.IsNullOrWhiteSpace(request.Script))
        {
            return ValidationResult.Failure("Script is required");
        }

        if (request.Script.Length > config.MaxScriptSize)
        {
            return ValidationResult.Failure($"Script size {request.Script.Length} exceeds maximum allowed size of {config.MaxScriptSize} bytes");
        }

        if (!Enum.IsDefined(typeof(ScriptType), request.ScriptType))
        {
            return ValidationResult.Failure("ScriptType is invalid");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return ValidationResult.Failure("WorkingDirectory is required");
        }

        if (!Guid.TryParse(request.ProjectId, out var projectId) || projectId == Guid.Empty)
        {
            return ValidationResult.Failure("ProjectId must be a non-empty GUID");
        }

        if (!Guid.TryParse(request.NotebookId, out var notebookId) || notebookId == Guid.Empty)
        {
            return ValidationResult.Failure("NotebookId must be a non-empty GUID");
        }

        if (!string.IsNullOrWhiteSpace(request.GuideId)
            && (!Guid.TryParse(request.GuideId, out var guideId) || guideId == Guid.Empty))
        {
            return ValidationResult.Failure("GuideId must be a non-empty GUID when provided");
        }

        var environmentValidation = ValidateExecutionEnvironment(request.Environment);
        if (!environmentValidation.IsValid)
        {
            return environmentValidation;
        }

        if (request.TimeoutSeconds is < 1 or > 7200)
        {
            return ValidationResult.Failure("TimeoutSeconds must be between 1 and 7200 when provided.");
        }

        return ValidationResult.Success();
    }

    internal static Guid ResolveGuideScopeId(ScriptExecutionRequest request)
    {
        if (Guid.TryParse(request.GuideId, out var guideId) && guideId != Guid.Empty)
        {
            return guideId;
        }

        return Guid.Parse(request.NotebookId);
    }

    internal static ValidationResult ValidateExecutionEnvironment(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return ValidationResult.Success();
        }

        if (environment.Count > 128)
        {
            return ValidationResult.Failure("Environment contains too many entries");
        }

        foreach (var (key, value) in environment)
        {
            var keyValidation = ScriptExecutionScopeRuntime.ValidateEnvironmentKey(key);
            if (!keyValidation.IsValid)
            {
                return keyValidation;
            }

            if (value.Length > 64 * 1024)
            {
                return ValidationResult.Failure($"Environment value for '{key}' exceeds maximum size");
            }
        }

        return ValidationResult.Success();
    }

    internal static TimeSpan ResolveExecutionTimeout(ScriptExecutionRequest request, ScriptExecutionConfig config)
    {
        if (request.TimeoutSeconds is > 0)
        {
            return TimeSpan.FromSeconds(request.TimeoutSeconds.Value);
        }

        return config.MaxExecutionTime;
    }
}
