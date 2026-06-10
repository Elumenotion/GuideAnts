using System.Net;
using System.Net.Http.Json;
using System.Text;
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
        int? scriptType = null) => new
    {
        script,
        scriptType = scriptType ?? (int)ScriptType.Bash,
        workingDirectory = workingDirectory ?? notebook.WorkingDirectory,
        projectId = projectId ?? notebook.ProjectId.ToString(),
        notebookId = notebookId ?? notebook.NotebookId.ToString()
    };
}
