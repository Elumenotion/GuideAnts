using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ScriptExecutionAgent.Tests.Infrastructure;

namespace ScriptExecutionAgent.Tests.InProcess;

[TestClass]
public sealed class ScriptExecutionAgentAdminApiTests
{
    private ScriptExecutionAgentWebApplicationFactory? _factory;

    [TestCleanup]
    public void TearDown()
    {
        _factory?.Dispose();
    }

    [TestMethod]
    public async Task Admin_routes_return_404_when_disabled()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: false);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/health");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Admin_routes_require_separate_admin_token_when_enabled()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/health");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Admin_health_returns_ok_when_enabled_and_authenticated()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();

        var response = await client.GetAsync("/admin/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("OK");
    }

    [TestMethod]
    public async Task Admin_requirements_reject_blocked_sources()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        using var content = new StringContent("--index-url https://example.invalid/simple", Encoding.UTF8, "text/plain");

        var response = await client.PutAsync("/admin/requirements", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Admin_apt_packages_reject_options_and_paths()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        using var content = new StringContent("--allow-unauthenticated", Encoding.UTF8, "text/plain");

        var response = await client.PutAsync("/admin/apt-packages", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
