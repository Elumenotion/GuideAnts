using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ScriptExecutionAgent.Tests.Infrastructure;

namespace ScriptExecutionAgent.Tests.Integration;

[TestClass]
public sealed class ScriptExecutionAgentEndpointTests
{
    private ScriptExecutionAgentProcessHost _host = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        _host = new ScriptExecutionAgentProcessHost();
        await _host.StartAsync();
    }

    [TestCleanup]
    public async Task TearDownAsync()
    {
        await _host.DisposeAsync();
    }

    [TestMethod]
    public async Task Health_returns_ok()
    {
        using var client = _host.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("OK");
    }

    [TestMethod]
    public async Task Execute_without_token_returns_401()
    {
        using var client = _host.CreateClient();
        var body = CreateExecuteBody(_host.Notebook, "echo test");

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Execute_with_wrong_token_returns_401()
    {
        using var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Script-Agent-Token", "wrong-token");
        var body = CreateExecuteBody(_host.Notebook, "echo test");

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Execute_with_invalid_json_returns_400()
    {
        using var client = _host.CreateAuthenticatedClient();
        using var content = new StringContent("{ not-json", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/execute", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Execute_with_empty_script_returns_400()
    {
        using var client = _host.CreateAuthenticatedClient();
        var body = CreateExecuteBody(_host.Notebook, script: string.Empty);

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Script is required");
    }

    [TestMethod]
    public async Task Execute_with_invalid_project_id_returns_400()
    {
        using var client = _host.CreateAuthenticatedClient();
        var body = new
        {
            script = "echo test",
            scriptType = (int)ScriptType.Bash,
            workingDirectory = _host.Notebook.WorkingDirectory,
            projectId = "not-a-guid",
            notebookId = _host.Notebook.NotebookId.ToString(),
            guideId = _host.Notebook.GuideId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ProjectId");
    }

    [TestMethod]
    public async Task Execute_with_path_outside_storage_root_returns_400()
    {
        using var client = _host.CreateAuthenticatedClient();
        var outsidePath = OperatingSystem.IsWindows()
            ? @"C:\"
            : "/tmp";
        var body = CreateExecuteBody(_host.Notebook, "echo test", outsidePath);

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("FILE_STORAGE_ROOT");
    }

    [TestMethod]
    public async Task Execute_with_mismatched_notebook_metadata_returns_400()
    {
        using var client = _host.CreateAuthenticatedClient();
        var wrongNotebookId = Guid.NewGuid();
        var body = new
        {
            script = "echo test",
            scriptType = (int)ScriptType.Bash,
            workingDirectory = _host.Notebook.WorkingDirectory,
            projectId = _host.Notebook.ProjectId.ToString(),
            notebookId = wrongNotebookId.ToString(),
            guideId = _host.Notebook.GuideId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("notebook-scoped");
    }

    [TestMethod]
    public async Task Execute_happy_path_runs_script_when_interpreter_available()
    {
        if (!IsInterpreterAvailable("python"))
        {
            Assert.Inconclusive("python interpreter is not available on this machine.");
        }

        using var client = _host.CreateAuthenticatedClient();
        var body = new
        {
            script = "print('agent-ok')",
            scriptType = (int)ScriptType.Python,
            workingDirectory = _host.Notebook.WorkingDirectory,
            projectId = _host.Notebook.ProjectId.ToString(),
            notebookId = _host.Notebook.NotebookId.ToString(),
            guideId = _host.Notebook.GuideId.ToString()
        };

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(payload);
        var output = ReadStandardOutput(doc.RootElement);
        output.Should().NotBeNullOrWhiteSpace(payload);
        output.Should().Contain("agent-ok");
    }

    private static string? ReadStandardOutput(JsonElement root)
    {
        if (root.TryGetProperty("standardOutput", out var camel))
        {
            return camel.GetString();
        }

        if (root.TryGetProperty("StandardOutput", out var pascal))
        {
            return pascal.GetString();
        }

        if (root.TryGetProperty("stdout", out var shortName))
        {
            return shortName.GetString();
        }

        return null;
    }

    [TestMethod]
    public async Task Files_without_token_returns_401()
    {
        using var client = _host.CreateClient();
        var url = BuildFilesUrl(_host.Notebook);

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Files_with_unauthorized_directory_returns_400()
    {
        using var client = _host.CreateAuthenticatedClient();
        var outsidePath = OperatingSystem.IsWindows() ? @"C:\" : "/tmp";
        var url =
            $"/files?directory={Uri.EscapeDataString(outsidePath)}&projectId={_host.Notebook.ProjectId}&notebookId={_host.Notebook.NotebookId}";

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("FILE_STORAGE_ROOT");
    }

    [TestMethod]
    public async Task Files_lists_notebook_files()
    {
        _host.Notebook.CreateFile("sample.txt", "hello");
        using var client = _host.CreateAuthenticatedClient();
        var url = BuildFilesUrl(_host.Notebook, _host.Notebook.NotebookRoot);

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var files = await response.Content.ReadFromJsonAsync<string[]>();
        files.Should().NotBeNull();
        files!.Should().Contain("sample.txt");
    }

    private static string BuildFilesUrl(NotebookStorageFixture notebook, string? directory = null)
    {
        directory ??= notebook.WorkingDirectory;
        return
            $"/files?directory={Uri.EscapeDataString(directory)}&projectId={notebook.ProjectId}&notebookId={notebook.NotebookId}";
    }

    private static object CreateExecuteBody(NotebookStorageFixture notebook, string script, string? workingDirectory = null) =>
        new
        {
            script,
            scriptType = (int)ScriptType.Bash,
            workingDirectory = workingDirectory ?? notebook.WorkingDirectory,
            projectId = notebook.ProjectId.ToString(),
            notebookId = notebook.NotebookId.ToString(),
            guideId = notebook.GuideId.ToString()
        };

    private static bool IsInterpreterAvailable(string command)
    {
        try
        {
            var fileName = OperatingSystem.IsWindows() ? "where" : "which";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
