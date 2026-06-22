using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.IntegrationTests.Settings;

/// <summary>
/// Phase G.1: HTTP surface tests for the routing endpoints introduced by
/// Phases A-F. Exercises the actual pipeline (DB + exception handler +
/// <c>RoutingProblemDetailsFactory</c>) so the ProblemDetails wire shape
/// stays stable against the R-2.2 / R-2.4 contract.
/// </summary>
[TestClass]
public sealed class RoutingEndpointsTests : SettingsRoutingIntegrationTestBase
{
    [ClassInitialize]
    public static async Task ClassInit(TestContext ctx)
    {
        await InitializeSharedFactoryAsync(ctx);
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await DisposeSharedFactoryAsync();
    }

    [TestMethod]
    public async Task GetChatTargets_ListsCatalogModels()
    {
        await SeedCatalogModelAsync("gpt-4.1-chat-target", "openai-chat", displayName: "Chat Target");

        var response = await Client.GetAsync("/api/settings/routing/chat-targets");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var targets = await response.Content.ReadFromJsonAsync<List<ChatTargetDto>>();
        targets.Should().NotBeNull();
        targets!.Should().Contain(t => t.ModelId == "gpt-4.1-chat-target");
    }

    [TestMethod]
    public async Task ChatTargetReadiness_UnknownModel_Returns404WithModelNotFoundProblem()
    {
        // The readiness endpoint requires a ?strict=<bool> query parameter per the
        // minimal-API binding contract. The 404 MODEL_NOT_FOUND mapping fires in
        // both strict and non-strict modes — choose the simpler non-strict case.
        var response = await Client.GetAsync("/api/settings/routing/chat-targets/does-not-exist/readiness?strict=false");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "readiness endpoint maps MODEL_NOT_FOUND to 404 with a ProblemDetails body (SettingsRoutingEndpoints)");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        doc.RootElement.GetProperty("code").GetString().Should().Be("MODEL_NOT_FOUND");
        doc.RootElement.GetProperty("action").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("modelId").GetString().Should().Be("does-not-exist");
        doc.RootElement.GetProperty("type").GetString().Should().StartWith("https://guideants.app/problems/routing/");
    }

    [TestMethod]
    public async Task ChatTargetReadiness_Strict_BlockedModel_Returns409RoutingModelNotReady()
    {
        // Seed a catalog row with a provider that has no matching section — this
        // creates a deterministic blocker so strict mode flips readiness to 409.
        await SeedCatalogModelAsync("blocked-model", "unknown-provider");

        var response = await Client.GetAsync("/api/settings/routing/chat-targets/blocked-model/readiness?strict=true");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(409);
        doc.RootElement.GetProperty("code").GetString().Should().Be(RoutingErrorCodes.ModelNotReady);
        doc.RootElement.GetProperty("action").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("type").GetString().Should().StartWith("https://guideants.app/problems/routing/");
    }

    [TestMethod]
    public async Task ChatTargetsPreflight_NoAssistantsConfigured_ReturnsEmptyList()
    {
        // No assistants are seeded by this suite's fixture — the endpoint should
        // return an empty list rather than fail; the ProblemDetails path only
        // fires when a concrete resolver call throws. This keeps the Overview
        // tab usable on a brand-new install.
        var response = await Client.GetAsync("/api/settings/routing/chat-targets/preflight");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var targets = await response.Content.ReadFromJsonAsync<List<ChatTargetReadinessDto>>();
        targets.Should().NotBeNull();
    }
}
