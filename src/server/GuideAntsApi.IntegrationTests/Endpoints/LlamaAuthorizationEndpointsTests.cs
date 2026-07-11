using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class LlamaAuthorizationEndpointsTests : BaseEndpointTest
{
    private static readonly (HttpMethod Method, string Path, object? Body)[] LlamaRoutes =
    [
        (HttpMethod.Get, "/api/settings/llama/catalog", null),
        (HttpMethod.Get, "/api/settings/llama/catalog/qwen3.6-35b-a3b-mtp/quants?catalogVersion=2026-07-10", null),
        (HttpMethod.Get, "/api/settings/llama/router/entries", null),
        (HttpMethod.Put, "/api/settings/llama/router/entries/qwen-alias", new LlamaRouterEntryPutRequest("qwen-alias", "/m/a.gguf", "", new Dictionary<string, string> { ["ctx-size"] = "8192" }, "replace")),
        (HttpMethod.Get, "/api/settings/llama/runtime/inventory", null),
        (HttpMethod.Get, "/api/settings/llama/runtime/status", null),
        (HttpMethod.Get, "/api/settings/llama/installations/qwen-local", null),
        (HttpMethod.Post, "/api/settings/llama/installations/qwen-local/change-quant", new ChangeQuantRequestDto("q4_k_m", "abc")),
        (HttpMethod.Post, "/api/settings/llama/installations/qwen-local/repair", new RepairInstallationRequestDto(true)),
        (HttpMethod.Post, "/api/settings/llama/installations/qwen-local/adopt", new AdoptInstallationRequestDto("qwen3.6-35b-a3b", "2026-07-10", false)),
        (HttpMethod.Get, "/api/settings/llama/operations/11111111-1111-1111-1111-111111111111", null),
        (HttpMethod.Post, "/api/settings/models:add", new AddModelRequest(
            "llama-cpp",
            new AddModelCatalogDto("new-model", "Qwen", null, null, true),
            null,
            new AddModelInstallDto(
                LocalModelInstallSources.Curated,
                Curated: new AddModelInstallCuratedDto("qwen3.6-35b-a3b-mtp", "2026-07-10", "q6_k_xl", "abc123")))),
    ];

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        SetupAuthentication(Role.Admin);
    }

    [TestMethod]
    public async Task LlamaRoutes_Unauthenticated_Return401()
    {
        foreach (var (method, path, body) in LlamaRoutes)
        {
            Client.DefaultRequestHeaders.Authorization = null;
            using var request = BuildRequest(method, path, body);
            var response = await Client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"unauthenticated {method} {path}");
        }
    }

    [TestMethod]
    public async Task LlamaRoutes_NonAdmin_Return403()
    {
        foreach (var role in new[] { Role.Reader, Role.Contributor, Role.Pending })
        {
            foreach (var (method, path, body) in LlamaRoutes)
            {
                SetupAuthentication(role);
                using var request = BuildRequest(method, path, body);
                var response = await Client.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"role={role} {method} {path}");
            }
        }
    }

    [TestMethod]
    public async Task LlamaRoutes_Admin_PassAuthorizationGate()
    {
        SetupAuthentication(Role.Admin);

        foreach (var (method, path, body) in LlamaRoutes)
        {
            using var request = BuildRequest(method, path, body);
            var response = await Client.SendAsync(request);
            response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, $"admin {method} {path}");
            response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, $"admin {method} {path}");
        }
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private void SetupAuthentication(Role role)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(System.Security.Claims.ClaimTypes.Name, "Llama Auth Test"),
            new(System.Security.Claims.ClaimTypes.Email, "llama.auth@guideants.local"),
            new(System.Security.Claims.ClaimTypes.Role, role.ToString()),
        };
        var token = IntegrationTestAuthHandler.CreateToken(claims);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
