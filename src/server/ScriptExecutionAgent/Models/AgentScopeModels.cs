namespace ScriptExecutionAgent;

internal sealed record ScriptExecutionScopeOptions(
    string StateRootPath,
    string? PythonVenvRelativePath,
    string? PythonBootstrapCommand,
    bool RequireScopedPythonVenv,
    string? BasePythonVenvPath);

internal sealed record AdminApiOptions(
    bool Enabled,
    string? AdminToken,
    string StateDirectoryPath,
    bool FailOpen);

internal sealed record AdminApplyResult(
    string Status,
    int ScopesApplied,
    int ScopesSkipped,
    string[] Errors,
    AdminApplyResultDetails? Apt = null,
    AdminInstallScriptsApplyDetails? InstallScripts = null);

internal sealed record AdminApplyResultDetails(
    string Status,
    int Applied,
    int Skipped,
    string[] Errors);

internal sealed record ScriptProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal sealed record IsolatedProcessLaunchPlan(
    string Command,
    string[] Arguments,
    IReadOnlyDictionary<string, string?>? ProcessEnvironment);

internal sealed record ScriptExecutionScope(
    Guid ProjectId,
    Guid GuideScopeId,
    string ScopeRootPath,
    string ProjectRuntimeRootPath,
    string PythonVenvPath,
    string RequirementsFilePath,
    string AppliedStateFilePath)
{
    public string PythonExecutablePath =>
        OperatingSystem.IsWindows()
            ? Path.Combine(PythonVenvPath, "Scripts", "python.exe")
            : Path.Combine(PythonVenvPath, "bin", "python");
}

