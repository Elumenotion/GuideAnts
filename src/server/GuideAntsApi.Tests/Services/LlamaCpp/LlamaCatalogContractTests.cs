using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaCatalogContractTests
{
    internal static string ResolveContractsDirPublic() => ResolveContractsDir();

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
    public void CatalogFixture_DeserializesIntoDto()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "catalog-get-response.fixture.json"));
        var dto = JsonSerializer.Deserialize<LlamaCatalogResponseDto>(json, SerializerOptions);
        dto.Should().NotBeNull();
        dto!.SchemaVersion.Should().Be(1);
        dto.Task.Should().Be("llama");
        dto.CatalogVersion.Should().Be("2026-07-10");
        dto.Models.Should().HaveCount(1);
        dto.Models[0].Defaults.RouterPreset.Should().ContainKey("ctx-size");
        dto.Models[0].Defaults.RouterPreset.Should().ContainKey("image-min-tokens");
        dto.Models[0].Defaults.Mmproj.Should().NotBeNull();
        dto.Models[0].Defaults.Mmproj!.Path.Should().Be("mmproj-F16.gguf");
    }

    [TestMethod]
    public void AdminCatalogFixture_DeserializesIntoDto()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "admin-catalog-get-response.fixture.json"));
        var dto = JsonSerializer.Deserialize<LlamaCatalogResponseDto>(json, SerializerOptions);
        dto.Should().NotBeNull();
        dto!.Models[0].Id.Should().Be("qwen3.6-35b-a3b-mtp");
    }

    [TestMethod]
    public void QuantGroupFixture_DeserializesIntoDto()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "quant-group-response.fixture.json"));
        var dto = JsonSerializer.Deserialize<LlamaCatalogQuantsResponseDto>(json, SerializerOptions);
        dto.Should().NotBeNull();
        dto!.Quants.Should().HaveCount(2);
        dto.Quants[1].Files.Should().HaveCount(2);
        dto.Quants[1].Files[0].ShardIndex.Should().Be(1);
        dto.Projector.Should().NotBeNull();
        dto.Projector!.Path.Should().Be("mmproj-F16.gguf");
    }

    [TestMethod]
    public void AdminQuantsFixture_DeserializesIntoDto()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "admin-quants-get-response.fixture.json"));
        var dto = JsonSerializer.Deserialize<LlamaCatalogQuantsResponseDto>(json, SerializerOptions);
        dto.Should().NotBeNull();
        dto!.ResolvedRevision.Should().NotBeNullOrWhiteSpace();
        dto.Quants[0].Id.Should().Be("q6_k_xl");
    }

    [TestMethod]
    public async Task GetCatalogQuantsAsync_ForwardsResolvedTokenWithoutLoggingIt()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    File.ReadAllText(Path.Combine(ContractsDir, "admin-quants-get-response.fixture.json")),
                    new MediaTypeHeaderValue("application/json")),
            });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8086/"),
        };

        var client = new LlamaRuntimeAdminClient(httpClient, NullLogger<LlamaRuntimeAdminClient>.Instance);
        var result = await client.GetCatalogQuantsAsync("qwen3.6-35b-a3b-mtp", "2026-07-10", "secret-token");

        result.CatalogId.Should().Be("qwen3.6-35b-a3b-mtp");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.GetValues("X-HF-Token").Single().Should().Be("secret-token");
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Contain("/admin/catalog/qwen3.6-35b-a3b-mtp/quants");
    }

    [TestMethod]
    public void CatalogResponse_HasNoQuantAutoSelectionFields()
    {
        var json = File.ReadAllText(Path.Combine(ContractsDir, "catalog-get-response.fixture.json"));
        using var doc = JsonDocument.Parse(json);
        var forbidden = new[] { "selectedQuant", "defaultQuant", "preferredQuant", "selectedQuantId" };
        foreach (var name in forbidden)
        {
            doc.RootElement.TryGetProperty(name, out _).Should().BeFalse($"fixture must not expose {name}");
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }
}
