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

    public string StorageRoot { get; } = Path.Combine(
        Path.GetTempPath(),
        "script-agent-inprocess",
        Guid.NewGuid().ToString("N"));

    public NotebookStorageFixture Notebook { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(StorageRoot);
        Notebook = new NotebookStorageFixture(StorageRoot);

        Environment.SetEnvironmentVariable("FILE_STORAGE_ROOT", StorageRoot);
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_REQUIRE_TOKEN", "true");
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_AGENT_TOKEN", AgentToken);
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION", "false");
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_ALLOW_OWNERSHIP_FALLBACK", "true");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        builder.UseEnvironment("Development");
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Script-Agent-Token", AgentToken);
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
