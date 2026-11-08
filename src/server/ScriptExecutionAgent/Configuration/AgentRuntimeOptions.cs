namespace ScriptExecutionAgent;

/// <summary>
/// Agent runtime configuration resolved from the container environment at startup.
/// </summary>
internal sealed record AgentRuntimeOptions(
    string FileStorageRoot,
    ScriptExecutionConfig ScriptConfig,
    AgentSecurityOptions Security,
    ScriptExecutionScopeOptions ScopeOptions,
    AdminApiOptions AdminOptions)
{
    public static AgentRuntimeOptions FromEnvironment()
    {
        var executionTimeoutSeconds = AgentEnvironment.GetPositiveIntEnvironmentVariable(
            "SCRIPT_EXECUTION_TIMEOUT_SECONDS", defaultValue: 600);
        var scriptConfig = new ScriptExecutionConfig
        {
            MaxScriptSize = 1024 * 1024,
            MaxExecutionTime = TimeSpan.FromSeconds(executionTimeoutSeconds),
            MaxOutputSize = 1024 * 1024
        };

        var fileStorageRoot = Environment.GetEnvironmentVariable("FILE_STORAGE_ROOT")
            ?? throw new InvalidOperationException("FILE_STORAGE_ROOT environment variable is not configured");

        var requireAgentToken = AgentEnvironment.GetBooleanEnvironmentVariable(
            "SCRIPT_EXECUTION_REQUIRE_TOKEN", defaultValue: true);
        var allowOwnershipFallback = AgentEnvironment.GetBooleanEnvironmentVariable(
            "SCRIPT_EXECUTION_ALLOW_OWNERSHIP_FALLBACK",
            defaultValue: string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Development",
                StringComparison.OrdinalIgnoreCase));
        var enableNotebookIdentityIsolation = AgentEnvironment.GetBooleanEnvironmentVariable(
            "SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION", defaultValue: true);
        var agentToken = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_AGENT_TOKEN");

        var adminStateDir = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_STATE_DIR");
        if (string.IsNullOrWhiteSpace(adminStateDir))
        {
            adminStateDir = OperatingSystem.IsWindows()
                ? Path.Combine(fileStorageRoot, ".guideants", "script-agent-admin")
                : "/var/lib/guideants/script-agent-admin";
        }

        var adminOptions = new AdminApiOptions(
            AgentEnvironment.GetBooleanEnvironmentVariable(
                "SCRIPT_EXECUTION_ADMIN_API_ENABLED", defaultValue: false),
            Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_TOKEN"),
            Path.GetFullPath(adminStateDir),
            AgentEnvironment.GetBooleanEnvironmentVariable(
                "SCRIPT_EXECUTION_ADMIN_FAIL_OPEN", defaultValue: false));

        var scopeStateRoot = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_SCOPE_STATE_ROOT");
        if (string.IsNullOrWhiteSpace(scopeStateRoot))
        {
            scopeStateRoot = Path.Combine(fileStorageRoot, ".guideants", "script-execution");
        }

        var basePythonVenvPath = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_BASE_PYTHON_VENV");
        if (string.IsNullOrWhiteSpace(basePythonVenvPath) && OperatingSystem.IsLinux())
        {
            basePythonVenvPath = "/opt/venv";
        }

        var scopeOptions = new ScriptExecutionScopeOptions(
            Path.GetFullPath(scopeStateRoot),
            Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_SCOPE_PYTHON_VENV_DIR"),
            Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_PYTHON_BOOTSTRAP"),
            AgentEnvironment.GetBooleanEnvironmentVariable(
                "SCRIPT_EXECUTION_REQUIRE_SCOPED_VENV", defaultValue: OperatingSystem.IsLinux()),
            string.IsNullOrWhiteSpace(basePythonVenvPath) ? null : Path.GetFullPath(basePythonVenvPath));

        return new AgentRuntimeOptions(
            fileStorageRoot,
            scriptConfig,
            new AgentSecurityOptions(
                requireAgentToken, agentToken, allowOwnershipFallback, enableNotebookIdentityIsolation),
            scopeOptions,
            adminOptions);
    }

    public void Validate()
    {
        if (Security.RequireAgentToken && string.IsNullOrWhiteSpace(Security.AgentToken))
        {
            throw new InvalidOperationException(
                "SCRIPT_EXECUTION_AGENT_TOKEN must be configured when SCRIPT_EXECUTION_REQUIRE_TOKEN=true.");
        }

        if (AdminOptions.Enabled && string.IsNullOrWhiteSpace(AdminOptions.AdminToken))
        {
            throw new InvalidOperationException(
                "SCRIPT_EXECUTION_ADMIN_TOKEN must be configured when SCRIPT_EXECUTION_ADMIN_API_ENABLED=true.");
        }
    }

    public void LogStartupConfiguration(ILogger logger)
    {
        logger.LogInformation(
            "SECURITY: startup config tokenRequired={TokenRequired} tokenConfigured={TokenConfigured} storageRootConfigured={StorageRootConfigured} linuxIdentityIsolation={IdentityIsolation} allowOwnershipFallback={AllowOwnershipFallback} scopeStateRoot={ScopeStateRoot} requireScopedVenv={RequireScopedVenv} adminApiEnabled={AdminApiEnabled} adminTokenConfigured={AdminTokenConfigured} adminStateDir={AdminStateDir}",
            Security.RequireAgentToken,
            !string.IsNullOrWhiteSpace(Security.AgentToken),
            !string.IsNullOrWhiteSpace(FileStorageRoot),
            Security.EnableNotebookIdentityIsolation,
            Security.AllowOwnershipFallback,
            GuideAnts.Logging.LogValueSanitizer.Sanitize(ScopeOptions.StateRootPath),
            ScopeOptions.RequireScopedPythonVenv,
            AdminOptions.Enabled,
            !string.IsNullOrWhiteSpace(AdminOptions.AdminToken),
            GuideAnts.Logging.LogValueSanitizer.Sanitize(AdminOptions.StateDirectoryPath));
    }
}
