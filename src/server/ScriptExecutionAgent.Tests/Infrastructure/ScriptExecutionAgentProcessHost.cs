using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ScriptExecutionAgent.Tests.Infrastructure;

public sealed class ScriptExecutionAgentProcessHost : IAsyncDisposable
{
    public const string AgentToken = "integration-test-token";

    public string StorageRoot { get; } = Path.Combine(
        Path.GetTempPath(),
        "script-agent-tests",
        Guid.NewGuid().ToString("N"));

    public string RuntimeRoot { get; } = Path.Combine(
        Path.GetTempPath(),
        "script-agent-runtime-tests",
        Guid.NewGuid().ToString("N"));

    public NotebookStorageFixture Notebook { get; private set; } = null!;

    private Process? _process;
    private HttpClient? _client;
    private Task? _stdoutDrain;
    private Task? _stderrDrain;
    private string _baseAddress = string.Empty;

    public async Task StartAsync()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(RuntimeRoot);
        Notebook = new NotebookStorageFixture(StorageRoot);

        var port = GetFreeTcpPort();
        _baseAddress = $"http://127.0.0.1:{port}";
        var agentDll = typeof(ScriptExecutionRequest).Assembly.Location;

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{agentDll}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["FILE_STORAGE_ROOT"] = StorageRoot;
        startInfo.Environment["SCRIPT_EXECUTION_SCOPE_RUNTIME_ROOT"] = RuntimeRoot;
        startInfo.Environment["SCRIPT_EXECUTION_REQUIRE_TOKEN"] = "true";
        startInfo.Environment["SCRIPT_EXECUTION_AGENT_TOKEN"] = AgentToken;
        startInfo.Environment["SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION"] = "false";
        startInfo.Environment["SCRIPT_EXECUTION_ALLOW_OWNERSHIP_FALLBACK"] = "true";
        startInfo.Environment["SCRIPT_EXECUTION_REQUIRE_SCOPED_VENV"] = "false";
        startInfo.Environment["SCRIPT_EXECUTION_ADMIN_API_ENABLED"] = "false";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = _baseAddress;

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ScriptExecutionAgent process.");

        // Draining redirected streams avoids deadlocks when the agent logs verbose output during /execute.
        _stdoutDrain = DrainProcessStreamAsync(_process.StandardOutput);
        _stderrDrain = DrainProcessStreamAsync(_process.StandardError);

        await WaitForHealthAsync(TimeSpan.FromSeconds(30));

        _client = new HttpClient
        {
            BaseAddress = new Uri(_baseAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public HttpClient CreateClient() =>
        _client ?? throw new InvalidOperationException("ScriptExecutionAgent process has not been started.");

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        var requestClient = new HttpClient
        {
            BaseAddress = client.BaseAddress,
            Timeout = client.Timeout
        };
        requestClient.DefaultRequestHeaders.Add("X-Script-Agent-Token", AgentToken);
        return requestClient;
    }

    private async Task WaitForHealthAsync(TimeSpan timeout)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                var stderr = await _process.StandardError.ReadToEndAsync();
                var stdout = await _process.StandardOutput.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"ScriptExecutionAgent exited before becoming healthy. stdout={stdout} stderr={stderr}");
            }

            try
            {
                var response = await probe.GetAsync($"{_baseAddress}/health");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch
            {
                // Agent is still starting.
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"ScriptExecutionAgent did not become healthy within {timeout.TotalSeconds}s.");
    }

    private static async Task DrainProcessStreamAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is not null)
            {
            }
        }
        catch
        {
            // Best-effort drain while the agent process is running.
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort shutdown.
            }
        }

        _process?.Dispose();

        try
        {
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }

            if (Directory.Exists(RuntimeRoot))
            {
                Directory.Delete(RuntimeRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        await Task.CompletedTask;
    }
}
