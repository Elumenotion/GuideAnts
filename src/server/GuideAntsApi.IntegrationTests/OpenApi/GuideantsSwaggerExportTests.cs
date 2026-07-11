using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;

namespace GuideAntsApi.IntegrationTests.OpenApi;

[TestClass]
public sealed class GuideantsSwaggerExportTests
{
    private static TestWebApplicationFactory? _factory;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        _factory = new TestWebApplicationFactory();
        await _factory.InitializeAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }

    [TestMethod]
    public async Task ExportGuideantsSwagger_FromRunningApi()
    {
        using var client = _factory!.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("components", out var components).Should().BeTrue();
        components.TryGetProperty("securitySchemes", out _).Should().BeTrue();

        var paths = root.GetProperty("paths");
        paths.TryGetProperty("/api/settings/llama/catalog", out _).Should().BeTrue();
        paths.TryGetProperty("/api/settings/llama/runtime/inventory", out _).Should().BeTrue();
        paths.TryGetProperty("/api/settings/llama/installations/{modelId}/change-quant", out _).Should().BeTrue();
        paths.TryGetProperty("/api/settings/llama/runtime/status", out _).Should().BeTrue();

        var repoRoot = FindRepoRoot();
        var outputPath = Path.Combine(repoRoot, "guideants-swagger.json");
        await File.WriteAllTextAsync(outputPath, json);

        File.Exists(outputPath).Should().BeTrue();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GuideAntsApi.sln"))
                || Directory.Exists(Path.Combine(current.FullName, "src", "server")))
            {
                var candidate = Path.Combine(current.FullName, "src", "server", "GuideAntsApi.sln");
                if (File.Exists(candidate))
                {
                    return current.FullName;
                }
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
