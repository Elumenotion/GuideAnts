using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
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

    [TestMethod]
    public async Task Admin_config_routes_are_not_exposed()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();

        var getResponse = await client.GetAsync("/admin/config");
        using var content = new StringContent("{\"version\":1}", Encoding.UTF8, "application/json");
        var putResponse = await client.PutAsync("/admin/config", content);

        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        putResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Admin_apply_global_includes_apt_result_details()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();

        var result = await StartApplyAndWaitForResultAsync(client);

        result.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
        result.TryGetProperty("apt", out var apt).Should().BeTrue();
        apt.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task Admin_apply_returns_bad_request_with_clear_message_when_apt_apply_fails()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Deterministic apt failure assertion is only enforced on non-Linux test hosts.");
        }

        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        using var setAptContent = new StringContent("jq", Encoding.UTF8, "text/plain");
        var setAptResponse = await client.PutAsync("/admin/apt-packages", setAptContent);
        setAptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var applyResponse = await client.PostAsync("/admin/apply", content: null);

        applyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await applyResponse.Content.ReadAsStringAsync();
        body.Should().Contain("apt package apply is supported only on Linux containers.");
    }

    [TestMethod]
    public async Task Admin_apply_returns_bad_request_when_apt_reconcile_requires_removal_on_non_linux_host()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Deterministic apt remove-path assertion is only enforced on non-Linux test hosts.");
        }

        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        using var setAptContent = new StringContent(string.Empty, Encoding.UTF8, "text/plain");
        var setAptResponse = await client.PutAsync("/admin/apt-packages", setAptContent);
        setAptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var adminStateDir = Path.Combine(_factory.StorageRoot, ".guideants", "script-agent-admin");
        Directory.CreateDirectory(adminStateDir);
        var statePath = Path.Combine(adminStateDir, "applied-state.json");
        var seededState = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["version"] = "1",
            ["aptPackagesHash"] = "seed-legacy-hash",
            ["aptManagedPackages"] = "jq"
        });
        File.WriteAllText(statePath, seededState);

        var applyResponse = await client.PostAsync("/admin/apply", content: null);

        applyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await applyResponse.Content.ReadAsStringAsync();
        body.Should().Contain("apt package apply is supported only on Linux containers.");
    }

    [TestMethod]
    public async Task Admin_apply_scoped_reconciles_unmanaged_python_package_when_requirements_hash_is_unchanged()
    {
        if (!PythonVenvTestHelper.CanCreateScopedPythonVenv())
        {
            Assert.Inconclusive("Scoped Python venv with pip is not available on this machine.");
        }

        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var adminClient = _factory.CreateAdminClient();
        using var executionClient = _factory.CreateAuthenticatedClient();

        var scopedAdminUrl = BuildScopedAdminUrl(_factory.Notebook);
        using var setRequirementsContent = new StringContent(string.Empty, Encoding.UTF8, "text/plain");
        var setRequirementsResponse = await adminClient.PutAsync($"/admin/requirements{scopedAdminUrl}", setRequirementsContent);
        setRequirementsResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var firstApplyResponse = await adminClient.PostAsync($"/admin/apply{scopedAdminUrl}", content: null);
        if (firstApplyResponse.StatusCode == HttpStatusCode.BadRequest)
        {
            var firstApplyBody = await firstApplyResponse.Content.ReadAsStringAsync();
            if (firstApplyBody.Contains("Failed to create scoped Python virtual environment", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive("Scoped Python venv could not be provisioned on this host.");
            }
        }

        firstApplyResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitForApplyJobResultAsync(
            adminClient,
            JsonDocument.Parse(await firstApplyResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetString()!);

        var packageName = $"ga_reconcile_probe_{Guid.NewGuid():N}".ToLowerInvariant();
        var createPackageScript = $$"""
import site
from pathlib import Path

name = "{{packageName}}"
site_packages = Path(site.getsitepackages()[0])
dist_info = site_packages / f"{name}-0.1.0.dist-info"
dist_info.mkdir(parents=True, exist_ok=True)

(site_packages / f"{name}.py").write_text("VALUE = 'probe'\n", encoding="utf-8")
(dist_info / "METADATA").write_text(
    "Metadata-Version: 2.1\n"
    f"Name: {name}\n"
    "Version: 0.1.0",
    encoding="utf-8",
)
(dist_info / "WHEEL").write_text(
    "Wheel-Version: 1.0\n"
    "Generator: guideants-test\n"
    "Root-Is-Purelib: true\n"
    "Tag: py3-none-any",
    encoding="utf-8",
)
(dist_info / "INSTALLER").write_text("pip", encoding="utf-8")
(dist_info / "RECORD").write_text(
    f"{name}.py,,\n"
    f"{name}-0.1.0.dist-info/METADATA,,\n"
    f"{name}-0.1.0.dist-info/WHEEL,,\n"
    f"{name}-0.1.0.dist-info/INSTALLER,,\n"
    f"{name}-0.1.0.dist-info/RECORD,,",
    encoding="utf-8",
)
print("created")
""";

        var createPackageResponse = await executionClient.PostAsJsonAsync(
            "/execute",
            CreateExecuteBody(_factory.Notebook, createPackageScript, scriptType: (int)ScriptType.Python));

        createPackageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var createDoc = JsonDocument.Parse(await createPackageResponse.Content.ReadAsStringAsync()))
        {
            ReadStandardOutput(createDoc.RootElement).Should().Contain("created");
        }

        var probeInstalledResponse = await executionClient.PostAsJsonAsync(
            "/execute",
            CreateExecuteBody(
                _factory.Notebook,
                $"import importlib.util\nprint('present' if importlib.util.find_spec('{packageName}') else 'missing')",
                scriptType: (int)ScriptType.Python));
        probeInstalledResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var installedDoc = JsonDocument.Parse(await probeInstalledResponse.Content.ReadAsStringAsync()))
        {
            ReadStandardOutput(installedDoc.RootElement).Should().Be("present");
        }

        var applyResult = await StartApplyAndWaitForResultAsync(adminClient, scopedAdminUrl);
        applyResult.GetProperty("status").GetString().Should().Be("applied");

        var probeRemovedResponse = await executionClient.PostAsJsonAsync(
            "/execute",
            CreateExecuteBody(
                _factory.Notebook,
                $"import importlib.util\nprint('present' if importlib.util.find_spec('{packageName}') else 'missing')",
                scriptType: (int)ScriptType.Python));
        probeRemovedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var removedDoc = JsonDocument.Parse(await probeRemovedResponse.Content.ReadAsStringAsync()))
        {
            ReadStandardOutput(removedDoc.RootElement).Should().Be("missing");
        }
    }

    [TestMethod]
    public async Task Admin_apply_scoped_preflight_rejects_missing_python_package()
    {
        if (!PythonVenvTestHelper.CanCreateScopedPythonVenv())
        {
            Assert.Inconclusive("Scoped Python venv with pip is not available on this machine.");
        }

        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var adminClient = _factory.CreateAdminClient();
        var scopedAdminUrl = BuildScopedAdminUrl(_factory.Notebook);
        var missingPackage = $"ga-missing-package-{Guid.NewGuid():N}";
        using var setRequirementsContent = new StringContent(missingPackage, Encoding.UTF8, "text/plain");
        var setRequirementsResponse = await adminClient.PutAsync($"/admin/requirements{scopedAdminUrl}", setRequirementsContent);
        setRequirementsResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var applyResponse = await adminClient.PostAsync($"/admin/apply{scopedAdminUrl}", content: null);
        if (applyResponse.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await applyResponse.Content.ReadAsStringAsync();
            if (body.Contains("Failed to create scoped Python virtual environment", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive("Scoped Python venv could not be provisioned on this host.");
            }

            body.Should().Contain("pip install failed");
            return;
        }

        applyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Admin_install_scripts_reject_invalid_json()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        var scopedAdminUrl = BuildScopedAdminUrl(_factory.Notebook);
        using var content = new StringContent("{not-json", Encoding.UTF8, "application/json");

        var response = await client.PutAsync($"/admin/install-scripts{scopedAdminUrl}", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("valid JSON");
    }

    [TestMethod]
    public async Task Admin_install_scripts_reject_invalid_python_syntax()
    {
        if (!PythonVenvTestHelper.CanCreateScopedPythonVenv())
        {
            Assert.Inconclusive("Scoped Python venv with pip is not available on this machine.");
        }

        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        var scopedAdminUrl = BuildScopedAdminUrl(_factory.Notebook);
        var payload = JsonSerializer.Serialize(new
        {
            version = 1,
            scripts = new[]
            {
                new
                {
                    name = "bad syntax",
                    scriptType = "Python",
                    script = "def broken(:\n    pass"
                }
            }
        });
        using var setContent = new StringContent(payload, Encoding.UTF8, "application/json");
        var setResponse = await client.PutAsync($"/admin/install-scripts{scopedAdminUrl}", setContent);
        setResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var applyResponse = await client.PostAsync($"/admin/apply{scopedAdminUrl}", content: null);
        if (applyResponse.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await applyResponse.Content.ReadAsStringAsync();
            if (body.Contains("Failed to create scoped Python virtual environment", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive("Scoped Python venv could not be provisioned on this host.");
            }

            body.Should().Contain("syntax validation");
            return;
        }

        applyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task Admin_setup_status_returns_overall_state_for_scope()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();
        var scopedAdminUrl = BuildScopedAdminUrl(_factory.Notebook);

        var response = await client.GetAsync($"/admin/setup-status{scopedAdminUrl}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("overallStatus").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("scopeKey").GetString().Should().Contain("project:");
        document.RootElement.TryGetProperty("requirements", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("installScripts", out _).Should().BeTrue();
    }

    [TestMethod]
    public async Task Admin_apply_returns_accepted_and_job_status_endpoint_tracks_completion()
    {
        _factory = new ScriptExecutionAgentWebApplicationFactory(enableAdminApi: true);
        using var client = _factory.CreateAdminClient();

        var startResponse = await client.PostAsync("/admin/apply", content: null);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var acceptedDoc = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var jobId = acceptedDoc.RootElement.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();

        var result = await WaitForApplyJobResultAsync(client, jobId!);
        result.GetProperty("status").GetString().Should().Be("succeeded");
        result.TryGetProperty("result", out _).Should().BeTrue();
    }

    private static async Task<JsonElement> StartApplyAndWaitForResultAsync(
        HttpClient client,
        string? scopedUrl = null,
        TimeSpan? timeout = null)
    {
        var startResponse = await client.PostAsync("/admin/apply" + (scopedUrl ?? string.Empty), content: null);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var acceptedDoc = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        var jobId = acceptedDoc.RootElement.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();

        var statusDoc = await WaitForApplyJobResultAsync(client, jobId!, timeout);
        return statusDoc.GetProperty("result");
    }

    private static async Task<JsonElement> WaitForApplyJobResultAsync(
        HttpClient client,
        string jobId,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        while (DateTime.UtcNow < deadline)
        {
            var statusResponse = await client.GetAsync($"/admin/apply/jobs/{jobId}");
            statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var statusDoc = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            var status = statusDoc.RootElement.GetProperty("status").GetString();
            if (status is "succeeded" or "failed")
            {
                if (status == "failed")
                {
                    var error = statusDoc.RootElement.GetProperty("error").GetString();
                    throw new InvalidOperationException(error ?? "Apply job failed.");
                }

                return statusDoc.RootElement;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Apply job {jobId} did not complete.");
    }

    private static string BuildScopedAdminUrl(NotebookStorageFixture notebook) =>
        $"?projectId={notebook.ProjectId:D}&guideId={notebook.GuideId:D}";

    private static object CreateExecuteBody(NotebookStorageFixture notebook, string script, int scriptType) => new
    {
        script,
        scriptType,
        workingDirectory = notebook.WorkingDirectory,
        projectId = notebook.ProjectId.ToString(),
        notebookId = notebook.NotebookId.ToString(),
        guideId = notebook.GuideId.ToString()
    };

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
}
