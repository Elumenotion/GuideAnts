using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaRouterContractTests
{
    private static readonly string ContractsDir = ResolveContractsDir();

    private static string ResolveContractsDir()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "docs", "llama-router-preset-ui-execution", "contracts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate docs/llama-router-preset-ui-execution/contracts");
    }

    [TestMethod]
    public void RouterEntryGetFixture_DeserializesIntoDto()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "router-entry-get-response.fixture.json"));
        var dto = JsonSerializer.Deserialize<LlamaRouterEntriesResponseDto>(json, SerializerOptions);
        dto.Should().NotBeNull();
        dto!.Entries.Should().HaveCount(1);
        dto.Entries[0].Preset.Should().ContainKey("spec-type");
    }

    [TestMethod]
    public void RouterEntryPutFixture_DeserializesIntoDto()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "router-entry-put-request.fixture.json"));
        var dto = JsonSerializer.Deserialize<LlamaRouterEntryPutRequest>(json, SerializerOptions);
        dto.Should().NotBeNull();
        dto!.PresetMode.Should().Be("replace");
    }

    [TestMethod]
    public void AdminRouterPostFixture_DeserializesIntoAdminRequest()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "admin-router-entries-post-request.fixture.json"));
        var dto = JsonSerializer.Deserialize<LlamaAdminRouterEntryUpsertRequest>(json, SerializerOptions);
        dto.Should().NotBeNull();
        dto!.Preset.Should().ContainKey("ctx-size");
    }

    [TestMethod]
    public async Task PutRouterEntryAsync_MapsGuideAntsPutToAdminPost()
    {
        var fixture = File.ReadAllText(Path.Combine(ContractsDir, "router-entry-put-request.fixture.json"));
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"iniSha256\":\"deadbeef\"}", Encoding.UTF8, "application/json"),
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8086/") };
        var client = new LlamaRuntimeAdminClient(httpClient, NullLogger<LlamaRuntimeAdminClient>.Instance);
        var request = JsonSerializer.Deserialize<LlamaRouterEntryPutRequest>(fixture, SerializerOptions)!;

        var result = await client.PutRouterEntryAsync(request);

        result.Ok.Should().BeTrue();
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().Should().Be("http://localhost:8086/router/entries");
        handler.LastRequestBody.Should().Contain("\"presetMode\":\"replace\"");
        handler.LastRequestBody.Should().Contain("\"ctx-size\":\"131072\"");
    }

    [TestMethod]
    public async Task GetRouterEntriesAsync_ParsesWrappedEntriesFixture()
    {
        var fixture = File.ReadAllText(Path.Combine(ContractsDir, "admin-router-entries-get-response.fixture.json"));
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fixture, Encoding.UTF8, "application/json"),
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8086/") };
        var client = new LlamaRuntimeAdminClient(httpClient, NullLogger<LlamaRuntimeAdminClient>.Instance);

        var response = await client.GetRouterEntriesAsync();

        response.Entries.Should().HaveCount(1);
        response.Entries[0].Preset.Should().ContainKey("spec-type");
    }

    [TestMethod]
    public void AdminDownloadsPostFixture_DeserializesIntoExactRequest()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "admin-downloads-post-request.fixture.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("operationId").GetString().Should().NotBeNullOrWhiteSpace();
        var immutable = root.GetProperty("immutableInput");
        immutable.GetProperty("modelFiles").GetArrayLength().Should().Be(2);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return responder(request);
        }
    }
}
