using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ScriptExecutionAgent.Tests.Infrastructure;

namespace ScriptExecutionAgent.Tests.InProcess;

[TestClass]
public sealed class RequestBodyLimitsTests
{
    private ScriptExecutionAgentWebApplicationFactory? _factory;

    [TestCleanup]
    public void TearDown()
    {
        _factory?.Dispose();
    }

    [TestMethod]
    public async Task Requirements_put_over_cap_returns_413()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        var body = new StringBuilder().Append('x', (int)AgentRequestBody.RequirementsBodyMaxBytes + 1).ToString();
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");

        var response = await client.PutAsync("/admin/requirements", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).Should().Be("Request body too large");
    }

    [TestMethod]
    public async Task Apt_packages_put_over_cap_returns_413()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        var body = new StringBuilder().Append('x', (int)AgentRequestBody.AptPackagesBodyMaxBytes + 1).ToString();
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");

        var response = await client.PutAsync("/admin/apt-packages", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).Should().Be("Request body too large");
    }

    [TestMethod]
    public async Task Invalid_json_still_returns_plain_text_400()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory();
        using var client = _factory.CreateAuthenticatedClient();
        using var content = new StringContent("{ not-json", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/execute", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().StartWith("JSON parsing error:");
    }

    [TestMethod]
    public async Task Empty_and_whitespace_json_bodies_return_invalid_request_body_400()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory();
        using var client = _factory.CreateAuthenticatedClient();

        using (var emptyContent = new StringContent(string.Empty, Encoding.UTF8, "application/json"))
        {
            var emptyResponse = await client.PostAsync("/execute", emptyContent);
            emptyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await emptyResponse.Content.ReadAsStringAsync()).Should().Be("Invalid request body");
        }

        using (var whitespaceContent = new StringContent("   \n  ", Encoding.UTF8, "application/json"))
        {
            var whitespaceResponse = await client.PostAsync("/execute", whitespaceContent);
            whitespaceResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await whitespaceResponse.Content.ReadAsStringAsync()).Should().Be("Invalid request body");
        }
    }

    [TestMethod]
    public async Task Install_scripts_put_near_cap_valid_document_accepted()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        var scopeUrl = BuildScopedAdminUrl(_factory.Notebook);

        var scriptBytes = new string('#', 256 * 1024);
        var document = new
        {
            version = 1,
            scripts = Enumerable.Range(0, 32)
                .Select(index => new
                {
                    id = $"script-{index}",
                    scriptType = "Python",
                    script = scriptBytes
                })
                .ToArray()
        };
        var payload = JsonSerializer.Serialize(document);
        payload.Length.Should().BeGreaterThan(8 * 1024 * 1024);
        payload.Length.Should().BeLessThan((int)AgentRequestBody.InstallScriptsBodyMaxBytes);

        using var setContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var setResponse = await client.PutAsync($"/admin/install-scripts{scopeUrl}", setContent);
        setResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync($"/admin/install-scripts{scopeUrl}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var roundTripped = await getResponse.Content.ReadFromJsonAsync<JsonDocument>();
        roundTripped.Should().NotBeNull();
        roundTripped!.RootElement.GetProperty("scripts").EnumerateArray().Count()
            .Should().Be(32);
        roundTripped.RootElement.GetProperty("scripts")[0].GetProperty("script").GetString()!.Length
            .Should().Be(scriptBytes.Length);
    }

    [TestMethod]
    public async Task Install_scripts_put_over_cap_returns_413()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        var body = new StringBuilder().Append('x', (int)AgentRequestBody.InstallScriptsBodyMaxBytes + 1).ToString();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await client.PutAsync($"/admin/install-scripts{BuildScopedAdminUrl(_factory.Notebook)}", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).Should().Be("Request body too large");
    }

    private static string BuildScopedAdminUrl(NotebookStorageFixture notebook) =>
        $"?projectId={notebook.ProjectId:D}&guideId={notebook.GuideId:D}";
}
