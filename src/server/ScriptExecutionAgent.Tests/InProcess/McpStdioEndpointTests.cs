using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ScriptExecutionAgent.Tests.Infrastructure;

namespace ScriptExecutionAgent.Tests.InProcess;

[TestClass]
public sealed class McpStdioEndpointTests
{
    private ScriptExecutionAgentWebApplicationFactory _factory = null!;
    private string _mockServerPath = null!;

    [TestInitialize]
    public void SetUp()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory();
        _mockServerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "Fixtures", "mock_mcp_stdio_server.py"));
        File.Exists(_mockServerPath).Should().BeTrue($"fixture missing: {_mockServerPath}");
    }

    [TestCleanup]
    public void TearDown() => _factory.Dispose();

    [TestMethod]
    public async Task McpStdio_happy_path_initialize_tools_call_teardown()
    {
        var client = _factory.CreateAuthenticatedClient();
        var body = BuildRequest(command: "python", args: [_mockServerPath], toolName: "demo_tool");

        var response = await client.PostAsJsonAsync("/mcp-stdio", body);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        payload.GetProperty("result").GetString().Should().Be("mock-result:demo_tool");
    }

    [TestMethod]
    public async Task McpStdio_spawn_failure_returns_explicit_error()
    {
        var client = _factory.CreateAuthenticatedClient();
        var body = BuildRequest(command: "/nonexistent/mcp-runner", args: [], toolName: "demo_tool");

        var response = await client.PostAsJsonAsync("/mcp-stdio", body);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("error").GetString().Should().Contain("failed");
    }

    [TestMethod]
    public async Task McpStdio_rejects_shell_string_command_injection_fields()
    {
        var client = _factory.CreateAuthenticatedClient();
        var body = BuildRequest(command: "python", args: [_mockServerPath], toolName: "demo_tool");
        body["command"] = "python; rm -rf /";

        var response = await client.PostAsJsonAsync("/mcp-stdio", body);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task McpStdio_scopes_guideId_in_request()
    {
        var client = _factory.CreateAuthenticatedClient();
        var guideId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var body = BuildRequest(command: "python", args: [_mockServerPath], toolName: "demo_tool", guideId: guideId);

        var response = await client.PostAsJsonAsync("/mcp-stdio", body);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
        body["guideId"]!.ToString().Should().Be(guideId.ToString("D"));
    }

    private Dictionary<string, object?> BuildRequest(
        string command,
        string[] args,
        string toolName,
        Guid? guideId = null)
    {
        var notebook = _factory.Notebook;
        return new Dictionary<string, object?>
        {
            ["projectId"] = notebook.ProjectId.ToString("D"),
            ["notebookId"] = notebook.NotebookId.ToString("D"),
            ["guideId"] = (guideId ?? notebook.GuideId).ToString("D"),
            ["workingDirectory"] = notebook.WorkingDirectory,
            ["command"] = command,
            ["arguments"] = args,
            ["toolName"] = toolName,
            ["timeoutSeconds"] = 30,
        };
    }
}
