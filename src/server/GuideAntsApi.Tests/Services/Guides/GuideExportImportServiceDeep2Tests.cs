using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.Guides.Skills;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideExportImportServiceDeep2Tests
{
    [TestMethod]
    [DataRow(0, "low")]
    [DataRow(1, "minimal")]
    [DataRow(2, "low")]
    [DataRow(3, "medium")]
    [DataRow(4, "high")]
    public async Task ImportAssistantAsync_Normalizes_numeric_reasoning_effort(int numeric, string expected)
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"deep2-reasoning-{numeric}-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(
            context,
            new TestDbContextFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            new AssistantSkillMetaSync(context));

        var name = $"Reasoning {numeric} {Guid.NewGuid():N}";
        await using var zip = CreateAssistantZip(name, reasoningEffort: numeric);

        var result = await service.ImportAssistantAsync(zip);

        result.Success.Should().BeTrue();
        var assistant = await context.Assistants.SingleAsync(a => a.Id == result.GuideId);
        assistant.ReasoningEffort.Should().Be(expected);
    }

    [TestMethod]
    public async Task ImportAssistantAsync_PreservesStringReasoningEffort_LowercasedPassthrough()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"deep2-reasoning-string-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(
            context,
            new TestDbContextFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            new AssistantSkillMetaSync(context));

        var name = $"Reasoning String {Guid.NewGuid():N}";
        await using var zip = CreateAssistantZip(name, reasoningEffortString: "high");

        var result = await service.ImportAssistantAsync(zip);

        var assistant = await context.Assistants.SingleAsync(a => a.Id == result.GuideId);
        assistant.ReasoningEffort.Should().Be("high");
    }

    [TestMethod]
    public async Task ImportAssistantAsync_ResolvesExistingCatalogModel_WithoutWarning()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"deep2-model-found-{Guid.NewGuid():N}");
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Models.Add(new Model
            {
                ModelId = "gpt-real",
                DisplayName = "GPT Real",
                Provider = "openai-chat",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(
            context,
            new TestDbContextFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            new AssistantSkillMetaSync(context));

        var name = $"Model Found {Guid.NewGuid():N}";
        await using var zip = CreateAssistantZip(name, model: "gpt-real");

        var result = await service.ImportAssistantAsync(zip);

        result.Success.Should().BeTrue();
        result.Warnings.Should().NotContain(w => w.Contains("catalog model", StringComparison.OrdinalIgnoreCase));
        var assistant = await context.Assistants.SingleAsync(a => a.Id == result.GuideId);
        assistant.ModelId.Should().Be("gpt-real");
    }

    [TestMethod]
    public async Task ImportAssistantAsync_ImportsOAuthProvider_WithScopes()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"deep2-oauth-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(
            context,
            new TestDbContextFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            new AssistantSkillMetaSync(context));

        await using var zip = CreateAssistantZipWithOAuth($"OAuth Assistant {Guid.NewGuid():N}");

        var result = await service.ImportAssistantAsync(zip);

        result.Success.Should().BeTrue();
        var provider = await context.AssistantAuthProviders
            .Include(p => p.Scopes)
            .SingleAsync();
        provider.AuthType.Should().Be("oauth");
        provider.ClientId.Should().Be("oauth-client");
        provider.Scopes.Select(s => s.Scope).Should().BeEquivalentTo(["User.Read", "Mail.Read"]);
    }

    [TestMethod]
    public async Task ImportGuideAsync_MapsFileExtensionsToContentTypes()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"deep2-content-types-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(
            context,
            new TestDbContextFactory(options),
            new BackgroundJobTestHelpers.CapturingJobQueueService(),
            new AssistantSkillMetaSync(context));

        var guideName = $"Content Types {Guid.NewGuid():N}";
        await using var zip = CreateGuideZipWithVariedFiles(guideName);

        var result = await service.ImportGuideAsync(zip);

        result.Success.Should().BeTrue();
        var guide = await context.Assistants
            .Include(a => a.Files)
            .SingleAsync(a => a.Id == result.GuideId);

        string ContentTypeFor(string path) => guide.Files.Single(f => f.RelativePath == path).ContentType!;

        ContentTypeFor("notes.txt").Should().Be("text/plain");
        ContentTypeFor("readme.md").Should().Be("text/markdown");
        ContentTypeFor("data.json").Should().Be("application/json");
        ContentTypeFor("table.csv").Should().Be("text/csv");
        ContentTypeFor("page.html").Should().Be("text/html");
        ContentTypeFor("doc.pdf").Should().Be("application/pdf");
        ContentTypeFor("feed.xml").Should().Be("application/xml");
        ContentTypeFor("binary.bin").Should().Be("application/octet-stream");
        guide.AvatarContentType.Should().Be("image/png");
    }

    // ----- helpers -----

    private static MemoryStream CreateAssistantZip(
        string name,
        int? reasoningEffort = null,
        string? reasoningEffortString = null,
        string? model = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestData = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = "crew member",
                ["model"] = model,
                ["tools"] = Array.Empty<object>()
            };
            if (reasoningEffort.HasValue)
            {
                manifestData["reasoning_effort"] = reasoningEffort.Value;
            }
            else if (reasoningEffortString is not null)
            {
                manifestData["reasoning_effort"] = reasoningEffortString;
            }

            WriteTextEntry(archive, "manifest.json", JsonSerializer.Serialize(manifestData));
            WriteTextEntry(archive, "instructions.md", "Assist.");
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateAssistantZipWithOAuth(string name)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "manifest.json", JsonSerializer.Serialize(new
            {
                name,
                description = "oauth assistant",
                model = (string?)null,
                tools = Array.Empty<object>()
            }));
            WriteTextEntry(archive, "instructions.md", "Assist.");
            WriteTextEntry(
                archive,
                "OpenAPI/auth.json",
                """
                {
                  "hosts": {
                    "graph.microsoft.com": {
                      "auth_type": "oauth",
                      "client_id": "oauth-client",
                      "tenant": "common",
                      "scopes": ["User.Read", "Mail.Read"]
                    }
                  }
                }
                """);
            WriteTextEntry(archive, "OpenAPI/graph.json", CreateOpenApiSpec("https://graph.microsoft.com/v1.0"));
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateGuideZipWithVariedFiles(string guideName)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "manifest.json", JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = guideName,
                ["description"] = "varied files",
                ["crew"] = Array.Empty<object>()
            }));
            WriteTextEntry(archive, "instructions.md", "Do helpful things.");

            WriteTextEntry(archive, "CodeInterpreter/notes.txt", "txt");
            WriteTextEntry(archive, "CodeInterpreter/readme.md", "# md");
            WriteTextEntry(archive, "CodeInterpreter/data.json", "{}");
            WriteTextEntry(archive, "CodeInterpreter/table.csv", "a,b");
            WriteTextEntry(archive, "CodeInterpreter/page.html", "<html></html>");
            WriteTextEntry(archive, "CodeInterpreter/doc.pdf", "%PDF");
            WriteTextEntry(archive, "CodeInterpreter/feed.xml", "<x/>");
            WriteTextEntry(archive, "CodeInterpreter/binary.bin", "bytes");

            WriteBinaryEntry(archive, "HostExtensions/UI/avatar.png", [1, 2, 3]);
        }

        stream.Position = 0;
        return stream;
    }

    private static string CreateOpenApiSpec(string serverUrl) => $$"""
        {
          "openapi": "3.0.1",
          "info": { "title": "Test API", "version": "1.0.0" },
          "servers": [ { "url": "{{serverUrl}}" } ],
          "paths": {
            "/items": {
              "get": {
                "operationId": "listItems",
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private static void WriteTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void WriteBinaryEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path);
        using var entryStream = entry.Open();
        entryStream.Write(content, 0, content.Length);
    }
}
