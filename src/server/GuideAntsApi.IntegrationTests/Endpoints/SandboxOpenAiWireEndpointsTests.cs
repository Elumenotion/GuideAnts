using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.SandboxWireApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class SandboxOpenAiWireEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        _factory = new TestWebApplicationFactory();
        await _factory.InitializeAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
            _factory = null!;
        }
    }

    [TestInitialize]
    public void TestInitialize()
    {
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client.Dispose();
    }

    [TestMethod]
    public async Task GetModels_With_valid_sandbox_wire_jwt_returns_enabled_aliases()
    {
        var fixture = await SeedSandboxWireFixtureAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/sandbox/openai/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var modelIds = json.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        modelIds.Should().Contain("guide");
    }

    [TestMethod]
    public async Task GetModels_With_invalid_token_returns_401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/sandbox/openai/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-jwt");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task PostResponses_With_valid_token_returns_unsupported_feature()
    {
        var fixture = await SeedSandboxWireFixtureAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/internal/sandbox/openai/v1/responses")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { model = "guide", input = "hello" }, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not yet available");
    }

    private static async Task<SandboxWireFixture> SeedSandboxWireFixtureAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jwtService = scope.ServiceProvider.GetRequiredService<ISandboxWireJwtService>();

        var ownerGuideId = Guid.NewGuid();
        var targetAssistantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        var ownerGuide = new Assistant
        {
            Id = ownerGuideId,
            Name = $"Sandbox Owner {ownerGuideId:N}",
            Kind = AssistantKind.Guide,
            IsActive = true,
            IsGlobal = true,
            ModelId = "gpt-4.1",
            Instructions = "Owner guide",
            SandboxWireApiConfigJson = JsonSerializer.Serialize(new SandboxWireApiConfigDto
            {
                Enabled = true,
                TargetAssistantId = targetAssistantId,
                EndpointFlags = new PublishedWireApiEndpointFlagsDto
                {
                    Models = true,
                    ChatCompletions = true,
                    Responses = true,
                },
                AliasMap = new Dictionary<string, string> { ["guide"] = "guide" },
            }, JsonOptions),
        };

        var targetAssistant = new Assistant
        {
            Id = targetAssistantId,
            Name = $"Sandbox Target {targetAssistantId:N}",
            Kind = AssistantKind.Assistant,
            IsActive = true,
            IsGlobal = true,
            ModelId = "gpt-4.1",
            Instructions = "Target assistant",
        };

        if (!await db.Models.AnyAsync(m => m.ModelId == "gpt-4.1"))
        {
            db.Models.Add(new Model
            {
                ModelId = "gpt-4.1",
                Provider = "openai-chat",
                DisplayName = "GPT-4.1 Test",
                IsActive = true,
            });
        }

        db.Assistants.AddRange(ownerGuide, targetAssistant);
        db.Projects.Add(new Project
        {
            Id = projectId,
            Title = "Sandbox Wire Project",
            Slug = $"sandbox-wire-{projectId:N}",
            Created = DateTime.UtcNow,
        });
        db.Notebooks.Add(new Notebook
        {
            Id = notebookId,
            ProjectId = projectId,
            GuideId = ownerGuideId,
            Title = "Sandbox Wire Notebook",
            Slug = $"sandbox-wire-nb-{notebookId:N}",
            Created = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var issued = jwtService.Mint(new SandboxWireExecutionGrant(
            ExecutionId: executionId,
            ProjectId: projectId,
            NotebookId: notebookId,
            OwnerAssistantId: ownerGuideId,
            TargetAssistantId: targetAssistantId,
            TargetAssistantName: targetAssistant.Name,
            AllowedEndpoints: ["models", "chat.completions", "responses"],
            AttributionConversationId: null,
            AncestorAssistantIds: [ownerGuideId],
            Lifetime: TimeSpan.FromMinutes(10)));

        return new SandboxWireFixture(issued.Token);
    }

    private sealed record SandboxWireFixture(string Token);
}
