using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace ScriptExecutionAgent.Tests.Infrastructure;

/// <summary>
/// In-process host for coverlet instrumentation of ScriptExecutionAgent (vs external process tests).
/// </summary>
public sealed class ScriptExecutionAgentWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AgentToken = "inprocess-test-token";
    public const string AdminToken = "inprocess-admin-token";

    public string StorageRoot { get; } = Path.Combine(
        Path.GetTempPath(),
        "script-agent-inprocess",
        Guid.NewGuid().ToString("N"));

    public NotebookStorageFixture Notebook { get; private set; } = null!;

    public bool EnableIdentityIsolation { get; }

    public bool AllowOwnershipFallback { get; }

    public bool EnableAdminApi { get; }

    public string? BasePythonVenvPath { get; }

    public bool RequireScopedPythonVenv { get; }

    public ScriptExecutionAgentWebApplicationFactory(
        bool enableIdentityIsolation = false,
        bool allowOwnershipFallback = true,
        bool enableAdminApi = false,
        string? basePythonVenvPath = null,
        bool requireScopedPythonVenv = false)
    {
        EnableIdentityIsolation = enableIdentityIsolation;
        AllowOwnershipFallback = allowOwnershipFallback;
        EnableAdminApi = enableAdminApi;
        BasePythonVenvPath = basePythonVenvPath;
        RequireScopedPythonVenv = requireScopedPythonVenv;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(StorageRoot);
        Notebook = new NotebookStorageFixture(StorageRoot);

        Environment.SetEnvironmentVariable("FILE_STORAGE_ROOT", StorageRoot);
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_REQUIRE_TOKEN", "true");
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_AGENT_TOKEN", AgentToken);
        Environment.SetEnvironmentVariable(
            "SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION",
            EnableIdentityIsolation ? "true" : "false");
        Environment.SetEnvironmentVariable(
            "SCRIPT_EXECUTION_ALLOW_OWNERSHIP_FALLBACK",
            AllowOwnershipFallback ? "true" : "false");
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_API_ENABLED", EnableAdminApi ? "true" : "false");
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_TOKEN", EnableAdminApi ? AdminToken : string.Empty);
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_BASE_PYTHON_VENV", BasePythonVenvPath);
        Environment.SetEnvironmentVariable(
            "SCRIPT_EXECUTION_REQUIRE_SCOPED_VENV",
            RequireScopedPythonVenv ? "true" : "false");
        Environment.SetEnvironmentVariable(
            "SCRIPT_EXECUTION_ADMIN_STATE_DIR",
            Path.Combine(StorageRoot, ".guideants", "script-agent-admin"));
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        builder.UseEnvironment("Development");
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Script-Agent-Token", AgentToken);
        return client;
    }

    public HttpClient CreateAdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Script-Agent-Admin-Token", AdminToken);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        try
        {
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
