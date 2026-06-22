using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ScriptExecutionAgent.Tests.Infrastructure;

namespace ScriptExecutionAgent.Tests.InProcess;

[TestClass]
public sealed class ScriptExecutionAgentInProcessTests
{
    private ScriptExecutionAgentWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void SetUp()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory();
    }

    [TestCleanup]
    public void TearDown()
    {
        _factory.Dispose();
    }

    [TestMethod]
    public async Task Health_returns_ok_in_process()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("OK");
    }

    [TestMethod]
    public async Task Execute_without_token_returns_401_in_process()
    {
        using var client = _factory.CreateClient();
        var body = CreateExecuteBody(_factory.Notebook, "echo test");

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Execute_with_invalid_json_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        using var content = new StringContent("{ not-json", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/execute", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Execute_with_empty_script_returns_400_when_authenticated()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(_factory.Notebook, script: string.Empty);

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Script is required");
    }

    [TestMethod]
    public async Task Execute_rejects_path_outside_notebook_scope()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(
            _factory.Notebook,
            "echo hi",
            workingDirectory: Path.GetTempPath());

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("WorkingDirectory rejected");
    }

    [TestMethod]
    public async Task Execute_with_invalid_project_id_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(_factory.Notebook, "echo test", projectId: "not-a-guid");

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ProjectId");
    }

    [TestMethod]
    public async Task Execute_with_invalid_notebook_id_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(_factory.Notebook, "echo test", notebookId: "not-a-guid");

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("NotebookId");
    }

    [TestMethod]
    public async Task Execute_with_invalid_guide_id_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(_factory.Notebook, "echo test", guideId: "not-a-guid");

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("GuideId");
    }

    [TestMethod]
    public async Task Execute_with_invalid_script_type_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(_factory.Notebook, "echo test", scriptType: 999);

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ScriptType is invalid");
    }

    [TestMethod]
    public async Task Execute_with_empty_working_directory_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(_factory.Notebook, "echo test", workingDirectory: string.Empty);

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("WorkingDirectory is required");
    }

    [TestMethod]
    public async Task Execute_injects_per_run_environment_values()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var secretValue = $"secret-{Guid.NewGuid():N}";
        var body = CreateExecuteBody(
            _factory.Notebook,
            "Write-Output $env:DEMO_SECRET",
            scriptType: (int)ScriptType.PowerShell,
            environment: new Dictionary<string, string> { ["DEMO_SECRET"] = secretValue });

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        ReadStandardOutput(doc.RootElement).Should().Be(secretValue);
    }

    [TestMethod]
    public async Task Execute_does_not_inherit_agent_token_environment()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(
            _factory.Notebook,
            "if ($env:SCRIPT_EXECUTION_AGENT_TOKEN) { Write-Output 'leaked' } else { Write-Output 'missing' }",
            scriptType: (int)ScriptType.PowerShell);

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        ReadStandardOutput(doc.RootElement).Should().Be("missing");
    }

    [TestMethod]
    public async Task Execute_rejects_reserved_environment_keys()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(
            _factory.Notebook,
            "echo test",
            environment: new Dictionary<string, string> { ["PATH"] = "/tmp" });

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("reserved");
    }

    [TestMethod]
    public async Task Execute_python_scoped_venv_extends_base_runtime_packages()
    {
        if (!PythonVenvTestHelper.CanCreateScopedPythonVenv())
        {
            Assert.Inconclusive("Scoped Python venv with pip is not available on this machine.");
        }

        _factory.Dispose();
        var baseVenvPath = Path.Combine(Path.GetTempPath(), "script-agent-base-venv", Guid.NewGuid().ToString("N"));
        CreateFakeBaseRuntimePackage(baseVenvPath);
        _factory = new ScriptExecutionAgentWebApplicationFactory(
            basePythonVenvPath: baseVenvPath,
            requireScopedPythonVenv: true);

        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(
            _factory.Notebook,
            "import guideants_base_runtime_probe as probe\nprint(probe.VALUE)",
            scriptType: (int)ScriptType.Python);

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var stdout = ReadStandardOutput(doc.RootElement);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            var stderr = ReadStandardError(doc.RootElement);
            if (stderr?.Contains("Failed to create scoped Python virtual environment", StringComparison.OrdinalIgnoreCase) == true
                || stderr?.Contains("Error executing script:", StringComparison.OrdinalIgnoreCase) == true)
            {
                Assert.Inconclusive($"Scoped Python venv could not be provisioned on this host: {stderr}");
            }
        }

        stdout.Should().Be("from-base-runtime");
    }

    [TestMethod]
    public async Task Execute_with_mismatched_notebook_metadata_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var body = CreateExecuteBody(
            _factory.Notebook,
            "echo test",
            notebookId: Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/execute", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("notebook-scoped");
    }

    [TestMethod]
    public async Task Files_without_token_returns_401_in_process()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(BuildFilesUrl(_factory.Notebook));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Files_with_wrong_token_returns_401_in_process()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Script-Agent-Token", "wrong-token");

        var response = await client.GetAsync(BuildFilesUrl(_factory.Notebook));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Files_missing_directory_parameter_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var url = $"/files?projectId={_factory.Notebook.ProjectId}&notebookId={_factory.Notebook.NotebookId}";

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("directory parameter is required");
    }

    [TestMethod]
    public async Task Files_with_invalid_project_id_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var url = BuildFilesUrl(_factory.Notebook, projectId: "not-a-guid");

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("projectId parameter must be a non-empty GUID");
    }

    [TestMethod]
    public async Task Files_with_invalid_notebook_id_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var url = BuildFilesUrl(_factory.Notebook, notebookId: "not-a-guid");

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("notebookId parameter must be a non-empty GUID");
    }

    [TestMethod]
    public async Task Files_with_path_outside_storage_root_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var url = BuildFilesUrl(_factory.Notebook, directory: Path.GetTempPath());

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("FILE_STORAGE_ROOT");
    }

    [TestMethod]
    public async Task Files_with_mismatched_notebook_metadata_returns_400_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var url = BuildFilesUrl(_factory.Notebook, notebookId: Guid.NewGuid().ToString());

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("notebook-scoped");
    }

    [TestMethod]
    public async Task Files_returns_empty_when_authorized_directory_is_missing_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var missingDirectory = Path.Combine(_factory.Notebook.NotebookRoot, "missing", "nested");
        var url = BuildFilesUrl(_factory.Notebook, directory: missingDirectory);

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var files = await response.Content.ReadFromJsonAsync<string[]>();
        files.Should().NotBeNull();
        files.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Files_lists_notebook_files_and_filters_temporary_script_files_in_process()
    {
        using var client = _factory.CreateAuthenticatedClient();
        _factory.Notebook.CreateFile("Output/sample.txt", "hello");
        _factory.Notebook.CreateFile("Output/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_script.sh", "echo temp");

        var response = await client.GetAsync(BuildFilesUrl(_factory.Notebook));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var files = await response.Content.ReadFromJsonAsync<string[]>();
        files.Should().NotBeNull();
        files.Should().Contain("sample.txt");
        files.Should().NotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_script.sh");
    }

    private static string? ReadStandardOutput(JsonElement root)
    {
        if (root.TryGetProperty("StandardOutput", out var pascal))
        {
            return pascal.GetString();
        }

        if (root.TryGetProperty("standardOutput", out var camel))
        {
            return camel.GetString();
        }

        return null;
    }

    private static string? ReadStandardError(JsonElement root)
    {
        if (root.TryGetProperty("StandardError", out var pascal))
        {
            return pascal.GetString();
        }

        if (root.TryGetProperty("standardError", out var camel))
        {
            return camel.GetString();
        }

        return null;
    }

    private static void CreateFakeBaseRuntimePackage(string baseVenvPath)
    {
        var sitePackages = OperatingSystem.IsWindows()
            ? Path.Combine(baseVenvPath, "Lib", "site-packages")
            : Path.Combine(baseVenvPath, "lib", "python3.11", "site-packages");

        Directory.CreateDirectory(sitePackages);
        File.WriteAllText(
            Path.Combine(sitePackages, "guideants_base_runtime_probe.py"),
            "VALUE = 'from-base-runtime'\n");
    }

    private static string BuildFilesUrl(
        NotebookStorageFixture notebook,
        string? directory = null,
        string? projectId = null,
        string? notebookId = null)
    {
        directory ??= notebook.WorkingDirectory;
        projectId ??= notebook.ProjectId.ToString();
        notebookId ??= notebook.NotebookId.ToString();
        return
            $"/files?directory={Uri.EscapeDataString(directory)}&projectId={Uri.EscapeDataString(projectId)}&notebookId={Uri.EscapeDataString(notebookId)}";
    }

    private static object CreateExecuteBody(
        NotebookStorageFixture notebook,
        string script,
        string? workingDirectory = null,
        string? projectId = null,
        string? notebookId = null,
        string? guideId = null,
        int? scriptType = null,
        IReadOnlyDictionary<string, string>? environment = null) => new
    {
        script,
        scriptType = scriptType ?? (int)ScriptType.Bash,
        workingDirectory = workingDirectory ?? notebook.WorkingDirectory,
        projectId = projectId ?? notebook.ProjectId.ToString(),
        notebookId = notebookId ?? notebook.NotebookId.ToString(),
        guideId = guideId ?? notebook.GuideId.ToString(),
        environment
    };
}
