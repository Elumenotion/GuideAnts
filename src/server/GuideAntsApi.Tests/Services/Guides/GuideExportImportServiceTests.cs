using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideExportImportServiceTests
{
    [TestMethod]
    public async Task ExportGuideAsync_Throws_when_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"export-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var act = async () => await service.ExportGuideAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Guide not found*");
    }

    [TestMethod]
    public async Task PreviewImportAsync_Throws_for_empty_stream()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-preview-empty-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        await using var emptyZip = new MemoryStream();
        var act = async () => await service.PreviewImportAsync(emptyZip);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public async Task ExportGuideAsync_Returns_zip_for_existing_guide()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"export-guide-{Guid.NewGuid():N}");
        var guideId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Exportable Guide",
                Kind = AssistantKind.Guide,
                Description = "desc",
                Instructions = "help",
                ModelId = "gpt-4.1",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var bytes = await service.ExportGuideAsync(guideId);

        bytes.Should().NotBeNull();
        bytes!.Length.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task PreviewImportAsync_Throws_when_manifest_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-no-manifest-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        await using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("readme.txt");
        }
        zipStream.Position = 0;

        var act = async () => await service.PreviewImportAsync(zipStream);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*manifest.json not found*");
    }

    [TestMethod]
    public async Task Export_then_preview_reports_name_conflict_for_existing_guide()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-preview-{Guid.NewGuid():N}");
        var guideId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = $"Roundtrip Guide {guideId:N}",
                Kind = AssistantKind.Guide,
                Description = "desc",
                Instructions = "help",
                ModelId = "gpt-4.1",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var exportBytes = await service.ExportGuideAsync(guideId);
        exportBytes.Should().NotBeNull();

        await using var previewStream = new MemoryStream(exportBytes!);
        var preview = await service.PreviewImportAsync(previewStream);
        preview.GuideName.Should().Contain("Roundtrip Guide");
        preview.NameConflicts.Should().ContainSingle(c => c.Contains("Roundtrip Guide"));
    }

    [TestMethod]
    public async Task PreviewImportAsync_Parses_custom_assistants_and_conflicts()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"preview-import-assistants-{Guid.NewGuid():N}");
        var guideName = $"Preview Guide {Guid.NewGuid():N}";
        const string existingAssistantName = "Existing Assistant";

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Id = Guid.NewGuid(),
                Name = guideName,
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = Guid.NewGuid(),
                Name = existingAssistantName,
                Kind = AssistantKind.Assistant,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);
        await using var zip = CreateGuideZipWithAssistants(
            guideName,
            assistantNames: [existingAssistantName, "New Assistant"],
            crewNames: [existingAssistantName, "New Assistant"]);

        var preview = await service.PreviewImportAsync(zip);

        preview.GuideName.Should().Be(guideName);
        preview.CrewCount.Should().Be(2);
        preview.CustomAssistantCount.Should().Be(2);
        preview.NameConflicts.Should().BeEquivalentTo([guideName, existingAssistantName]);
    }

    [TestMethod]
    public async Task ImportGuideAsync_Throws_when_manifest_has_empty_name()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-empty-name-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        await using var zip = CreateGuideZip(manifestName: string.Empty);

        var act = async () => await service.ImportGuideAsync(zip);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Guide name is required*");
    }

    [TestMethod]
    public async Task ImportGuideAsync_Creates_guide_from_minimal_zip()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-create-{Guid.NewGuid():N}");
        var guideName = $"Imported {Guid.NewGuid():N}";
        var jobQueue = new BackgroundJobTestHelpers.CapturingJobQueueService();
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(context, new TestDbContextFactory(options), jobQueue);

        await using var zip = CreateGuideZip(guideName);

        var result = await service.ImportGuideAsync(zip);

        result.Success.Should().BeTrue();
        result.GuideId.Should().NotBe(Guid.Empty);
        (await context.Assistants.CountAsync(a => a.Name == guideName && a.Kind == AssistantKind.Guide))
            .Should().Be(1);
    }


    [TestMethod]
    public async Task ExportAssistantAsync_Throws_when_assistant_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"export-asst-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var act = async () => await service.ExportAssistantAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Assistant not found*");
    }

    [TestMethod]
    public async Task ExportAssistantAsync_Returns_zip_for_existing_assistant()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"export-asst-{Guid.NewGuid():N}");
        var assistantId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Id = assistantId,
                Name = "Crew Assistant",
                Kind = AssistantKind.Assistant,
                Description = "helper",
                Instructions = "help",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var bytes = await service.ExportAssistantAsync(assistantId);

        bytes.Should().NotBeEmpty();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        archive.GetEntry("manifest.json").Should().NotBeNull();
        archive.GetEntry("instructions.md").Should().NotBeNull();
    }

    [TestMethod]
    public async Task ImportAssistantAsync_Throws_when_assistant_name_already_exists()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-asst-dup-{Guid.NewGuid():N}");
        const string name = "Duplicate Assistant";
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant
            {
                Name = name,
                Kind = AssistantKind.Assistant,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);
        await using var zip = CreateAssistantZip(name);

        var act = async () => await service.ImportAssistantAsync(zip);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*'{name}' already exists*");
    }

    [TestMethod]
    public async Task ImportAssistantAsync_Creates_assistant_and_enqueues_shadow_jobs()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-asst-{Guid.NewGuid():N}");
        var jobQueue = new BackgroundJobTestHelpers.CapturingJobQueueService();
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(context, new TestDbContextFactory(options), jobQueue);
        var name = $"New Assistant {Guid.NewGuid():N}";
        await using var zip = CreateAssistantZip(name);

        var result = await service.ImportAssistantAsync(zip);

        result.Success.Should().BeTrue();
        (await context.Assistants.AnyAsync(a => a.Name == name && a.Kind == AssistantKind.Assistant))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task ImportAssistantAsync_Normalizes_numeric_reasoning_effort_in_manifest()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-asst-reasoning-{Guid.NewGuid():N}");
        var jobQueue = new BackgroundJobTestHelpers.CapturingJobQueueService();
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(context, new TestDbContextFactory(options), jobQueue);
        var name = $"Reasoning Assistant {Guid.NewGuid():N}";
        await using var zip = CreateAssistantZip(name, reasoningEffort: 4);

        var result = await service.ImportAssistantAsync(zip);

        result.Success.Should().BeTrue();
        var assistant = await context.Assistants.SingleAsync(a => a.Id == result.GuideId);
        assistant.ReasoningEffort.Should().Be("high");
    }

    [TestMethod]
    public async Task ExportGuideAsync_Writes_auth_and_assistant_payload_details()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"export-guide-rich-{Guid.NewGuid():N}");
        var guideId = Guid.NewGuid();
        var crewAssistantId = Guid.NewGuid();
        var toolId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            var model = new Model
            {
                ModelId = "gpt-4.1",
                DisplayName = "GPT 4.1",
                Provider = "openai-chat",
                Created = DateTime.UtcNow
            };

            var tool = new Tool
            {
                Id = toolId,
                ToolType = "file_search",
                DisplayName = "File Search",
                Created = DateTime.UtcNow
            };

            var oauthProvider = new AssistantAuthProvider
            {
                Id = Guid.NewGuid(),
                ProviderId = "graph.microsoft.com",
                AuthType = "oauth",
                ClientId = "oauth-client",
                Tenant = "common",
                UserConfigPolicy = "required",
                Created = DateTime.UtcNow,
                Scopes =
                [
                    new AssistantAuthScope { Scope = "User.Read" },
                    new AssistantAuthScope { Scope = "Mail.Read" }
                ]
            };

            var serviceHttpProvider = new AssistantAuthProvider
            {
                Id = Guid.NewGuid(),
                ProviderId = "api.search.example",
                AuthType = "service_http",
                HeaderName = "x-api-key",
                ValueTemplate = "secret-value",
                UserConfigPolicy = "required",
                Created = DateTime.UtcNow
            };

            var crewProvider = new AssistantAuthProvider
            {
                Id = Guid.NewGuid(),
                ProviderId = "crew.api.example",
                AuthType = "oauth",
                ClientId = "crew-client",
                Tenant = "organizations",
                Created = DateTime.UtcNow
            };

            var crewAssistant = new Assistant
            {
                Id = crewAssistantId,
                Name = "Analyst",
                Kind = AssistantKind.Assistant,
                Description = "Crew analyst",
                Instructions = "Crew instructions",
                ModelId = model.ModelId,
                Created = DateTime.UtcNow,
                ContextOptions =
                [
                    new AssistantContextOption { Key = "crew-mode", Value = "deep", Created = DateTime.UtcNow }
                ],
                ConversationStarters =
                [
                    new AssistantConversationStarter
                    {
                        Id = Guid.NewGuid(),
                        Prompt = "Investigate issue",
                        OrderIndex = 0,
                        Created = DateTime.UtcNow
                    }
                ],
                Files =
                [
                    new AssistantFile
                    {
                        FolderKind = "CodeInterpreter",
                        RelativePath = "crew-script.py",
                        ContentBytes = Encoding.UTF8.GetBytes("print('crew')"),
                        ContentType = "text/x-python",
                        Created = DateTime.UtcNow
                    }
                ],
                OpenApiSchemas =
                [
                    new AssistantOpenApiSchema
                    {
                        Name = "crew-api",
                        ApiHost = "crew.api.example",
                        SpecificationJson = CreateOpenApiSpec("https://crew.api.example/v1"),
                        AuthProvider = crewProvider,
                        Created = DateTime.UtcNow
                    }
                ],
                Tools =
                [
                    new AssistantTool
                    {
                        ToolId = toolId,
                        Tool = tool,
                        Created = DateTime.UtcNow
                    }
                ]
            };

            var guide = new Assistant
            {
                Id = guideId,
                Name = "Exportable Guide",
                Kind = AssistantKind.Guide,
                Description = "Rich export",
                Instructions = "Guide instructions",
                HomePageMarkdown = "# Home",
                ModelId = model.ModelId,
                InvocationEvaluator = "eval-assistant",
                AvatarImageBytes = [1, 2, 3],
                AvatarContentType = "image/png",
                Created = DateTime.UtcNow,
                ContextOptions =
                [
                    new AssistantContextOption { Key = "audience", Value = "engineering", Created = DateTime.UtcNow },
                    new AssistantContextOption { Key = "locale", Value = "en-US", Created = DateTime.UtcNow }
                ],
                ConversationStarters =
                [
                    new AssistantConversationStarter
                    {
                        Id = Guid.NewGuid(),
                        Prompt = "Second prompt",
                        OrderIndex = 2,
                        Created = DateTime.UtcNow
                    },
                    new AssistantConversationStarter
                    {
                        Id = Guid.NewGuid(),
                        Prompt = "First prompt",
                        OrderIndex = 0,
                        Created = DateTime.UtcNow
                    }
                ],
                Files =
                [
                    new AssistantFile
                    {
                        FolderKind = "VectorStore",
                        VectorStoreName = "KnowledgeBase",
                        RelativePath = "docs/guide.txt",
                        ContentBytes = Encoding.UTF8.GetBytes("guide content"),
                        ContentType = "text/plain",
                        Created = DateTime.UtcNow
                    },
                    new AssistantFile
                    {
                        FolderKind = "CodeInterpreter",
                        RelativePath = "scripts/main.py",
                        ContentBytes = Encoding.UTF8.GetBytes("print('hello')"),
                        ContentType = "text/x-python",
                        Created = DateTime.UtcNow
                    }
                ],
                OpenApiSchemas =
                [
                    new AssistantOpenApiSchema
                    {
                        Name = "graph",
                        ApiHost = "graph.microsoft.com",
                        SpecificationJson = CreateOpenApiSpec("https://graph.microsoft.com/v1.0"),
                        AuthProvider = oauthProvider,
                        Created = DateTime.UtcNow
                    },
                    new AssistantOpenApiSchema
                    {
                        Name = "search",
                        ApiHost = "api.search.example",
                        SpecificationJson = CreateOpenApiSpec("https://api.search.example/v1"),
                        AuthProvider = serviceHttpProvider,
                        Created = DateTime.UtcNow
                    }
                ],
                Tools =
                [
                    new AssistantTool
                    {
                        ToolId = toolId,
                        Tool = tool,
                        Created = DateTime.UtcNow
                    }
                ],
                CrewMembers =
                [
                    new GuideMember
                    {
                        GuideId = guideId,
                        AssistantId = crewAssistantId,
                        Assistant = crewAssistant,
                        DisplayOrder = 1,
                        Created = DateTime.UtcNow
                    }
                ]
            };

            seed.Models.Add(model);
            seed.Tools.Add(tool);
            seed.Assistants.AddRange(guide, crewAssistant);
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateExportImportService(context, options);

        var bytes = await service.ExportGuideAsync(guideId);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json");
        manifestEntry.Should().NotBeNull();
        using (var manifestReader = new StreamReader(manifestEntry!.Open()))
        {
            var manifest = JsonDocument.Parse(await manifestReader.ReadToEndAsync()).RootElement;
            manifest.GetProperty("name").GetString().Should().Be("Exportable Guide");
            manifest.GetProperty("defaultModel").GetString().Should().Be("gpt-4.1");
            manifest.GetProperty("invocationEvaluator").GetString().Should().Be("eval-assistant");
            manifest.GetProperty("tools")[0].GetProperty("type").GetString().Should().Be("file_search");
            manifest.GetProperty("crew")[0].GetProperty("name").GetString().Should().Be("Analyst");
        }

        var authEntry = archive.GetEntry("OpenAPI/auth.json");
        authEntry.Should().NotBeNull();
        using (var authReader = new StreamReader(authEntry!.Open()))
        {
            var hosts = JsonDocument.Parse(await authReader.ReadToEndAsync()).RootElement.GetProperty("hosts");
            hosts.GetProperty("graph.microsoft.com").GetProperty("auth_type").GetString().Should().Be("oauth");
            hosts.GetProperty("graph.microsoft.com").GetProperty("scopes").GetArrayLength().Should().Be(2);
            hosts.GetProperty("api.search.example").GetProperty("auth_type").GetString().Should().Be("service_http");
            hosts.GetProperty("api.search.example").GetProperty("header_value_env_var").GetString().Should().Be("••••••••");
        }

        var startersEntry = archive.GetEntry("HostExtensions/UI/conversationStarters.json");
        startersEntry.Should().NotBeNull();
        using (var startersReader = new StreamReader(startersEntry!.Open()))
        {
            var starters = JsonDocument.Parse(await startersReader.ReadToEndAsync()).RootElement
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToList();
            starters.Should().Equal(["First prompt", "Second prompt"]);
        }

        archive.GetEntry("HostExtensions/UI/contextOptions.json").Should().NotBeNull();
        archive.GetEntry("HostExtensions/UI/avatar.png").Should().NotBeNull();
        archive.GetEntry("VectorStores/KnowledgeBase/guide.txt").Should().NotBeNull();
        archive.GetEntry("CodeInterpreter/main.py").Should().NotBeNull();
        archive.GetEntry("assistants/Analyst/manifest.json").Should().NotBeNull();
        archive.GetEntry("assistants/Analyst/OpenAPI/auth.json").Should().NotBeNull();
    }

    [TestMethod]
    public async Task ImportGuideAsync_Imports_nested_assistants_files_and_warning_paths()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-guide-rich-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var globalAssistantId = Guid.NewGuid();
        var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Tools.Add(new Tool
            {
                Id = Guid.NewGuid(),
                ToolType = "file_search",
                DisplayName = "File Search",
                Created = DateTime.UtcNow
            });

            seed.Assistants.Add(new Assistant
            {
                Id = globalAssistantId,
                Name = "Global Crew",
                Kind = AssistantKind.Assistant,
                IsGlobal = true,
                Created = DateTime.UtcNow
            });

            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });

            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(context, new TestDbContextFactory(options), queue);
        var guideName = $"Imported Guide {Guid.NewGuid():N}";

        await using var zip = CreateGuideImportZipWithNestedAssistant(
            guideName,
            customAssistantName: "Embedded Crew",
            existingCrewName: "Global Crew",
            missingCrewName: "Missing Crew");

        var result = await service.ImportGuideAsync(zip);

        result.Success.Should().BeTrue();
        result.CustomAssistantsCreated.Should().Be(1);
        result.Warnings.Should().Contain(w => w.Contains("Missing Crew"));
        result.Warnings.Should().Contain(w => w.Contains("catalog model", StringComparison.OrdinalIgnoreCase));

        var importedGuide = await context.Assistants
            .Include(a => a.CrewMembers).ThenInclude(member => member.Assistant)
            .Include(a => a.ContextOptions)
            .Include(a => a.ConversationStarters)
            .Include(a => a.OpenApiSchemas).ThenInclude(schema => schema.AuthProvider)
            .Include(a => a.Files)
            .Include(a => a.Tools)
            .SingleAsync(a => a.Id == result.GuideId);

        importedGuide.Name.Should().Be(guideName);
        importedGuide.Kind.Should().Be(AssistantKind.Guide);
        importedGuide.ModelId.Should().BeNull();
        using (var authDoc = JsonDocument.Parse(importedGuide.AuthConfigJson!))
        {
            authDoc.RootElement.GetProperty("mode").GetString().Should().Be("oauth");
        }
        importedGuide.CrewMembers.Select(member => member.Assistant.Name)
            .Should()
            .BeEquivalentTo(["Global Crew", "Embedded Crew"]);
        importedGuide.ContextOptions.Should().ContainSingle(option => option.Key == "audience" && option.Value == "ops");
        importedGuide.ConversationStarters.Select(starter => starter.Prompt)
            .Should()
            .Equal(["Start here", "Go deeper"]);
        importedGuide.OpenApiSchemas.Should().ContainSingle(schema => schema.Name == "search");
        importedGuide.OpenApiSchemas.Single().AuthProvider.Should().NotBeNull();
        importedGuide.OpenApiSchemas.Single().AuthProvider!.HeaderName.Should().Be("x-api-key");
        importedGuide.Files.Should().Contain(file => file.FolderKind == "VectorStore" && file.RelativePath == "guide.txt");
        importedGuide.Files.Should().Contain(file => file.FolderKind == "CodeInterpreter" && file.RelativePath == "main.py");
        importedGuide.Tools.Should().ContainSingle();
        queue.Enqueued.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task ImportAssistantAsync_Converts_masked_service_http_values_to_required_policy()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"import-assistant-masked-{Guid.NewGuid():N}");
        var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();
        await using var context = new ApplicationDbContext(options);
        var service = new GuideExportImportService(context, new TestDbContextFactory(options), queue);

        await using var zip = CreateAssistantZipWithMaskedServiceHttpAuth("Masked Assistant");

        var result = await service.ImportAssistantAsync(zip);

        result.Success.Should().BeTrue();
        var provider = await context.AssistantAuthProviders.SingleAsync();
        provider.AuthType.Should().Be("service_http");
        provider.HeaderName.Should().Be("x-api-key");
        provider.ValueTemplate.Should().BeNull();
        provider.UserConfigPolicy.Should().Be("required");
    }

    private static MemoryStream CreateGuideImportZipWithNestedAssistant(
        string guideName,
        string customAssistantName,
        string existingCrewName,
        string missingCrewName)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJsonEntry(archive, "manifest.json", new Dictionary<string, object?>
            {
                ["name"] = guideName,
                ["description"] = "imported guide",
                ["defaultModel"] = "missing-model-id",
                ["defaultAssistant"] = customAssistantName,
                ["invocationEvaluator"] = "qa-evaluator",
                ["auth"] = new Dictionary<string, object?> { ["mode"] = "oauth" },
                ["tools"] = new[]
                {
                    new Dictionary<string, object?> { ["type"] = "file_search" }
                },
                ["crew"] = new[]
                {
                    new Dictionary<string, object?> { ["name"] = existingCrewName },
                    new Dictionary<string, object?> { ["name"] = customAssistantName },
                    new Dictionary<string, object?> { ["name"] = missingCrewName }
                }
            });

            WriteTextEntry(archive, "instructions.md", "Guide instructions");
            WriteTextEntry(archive, "HostExtensions/UI/home.md", "# Imported Home");
            WriteTextEntry(
                archive,
                "HostExtensions/UI/contextOptions.json",
                JsonSerializer.Serialize(new[] { new { key = "audience", value = "ops" } }));
            WriteTextEntry(
                archive,
                "HostExtensions/UI/conversationStarters.json",
                JsonSerializer.Serialize(new[] { "Start here", "Go deeper" }));
            WriteTextEntry(
                archive,
                "OpenAPI/auth.json",
                """
                {
                  "hosts": {
                    "api.search.example": {
                      "auth_type": "service_http",
                      "header_name": "x-api-key",
                      "header_value_env_var": "SEARCH_KEY"
                    }
                  }
                }
                """);
            WriteTextEntry(archive, "OpenAPI/search.json", CreateOpenApiSpec("https://api.search.example/v1"));
            WriteTextEntry(archive, "VectorStores/Knowledge/guide.txt", "vector content");
            WriteTextEntry(archive, "CodeInterpreter/main.py", "print('guide')");

            WriteTextEntry(
                archive,
                $"assistants/{customAssistantName}/manifest.json",
                JsonSerializer.Serialize(new
                {
                    name = customAssistantName,
                    description = "embedded assistant",
                    model = (string?)null,
                    tools = Array.Empty<object>()
                }));
            WriteTextEntry(
                archive,
                $"assistants/{customAssistantName}/instructions.md",
                "Embedded instructions");
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateAssistantZipWithMaskedServiceHttpAuth(string name)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(
                archive,
                "manifest.json",
                JsonSerializer.Serialize(new
                {
                    name,
                    description = "service http assistant",
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
                    "api.masked.example": {
                      "auth_type": "service_http",
                      "header_name": "x-api-key",
                      "header_value_env_var": "••••••••"
                    }
                  }
                }
                """);
            WriteTextEntry(archive, "OpenAPI/masked.json", CreateOpenApiSpec("https://api.masked.example/v1"));
        }

        stream.Position = 0;
        return stream;
    }

    private static string CreateOpenApiSpec(string serverUrl)
    {
        return $$"""
            {
              "openapi": "3.0.1",
              "info": {
                "title": "Test API",
                "version": "1.0.0"
              },
              "servers": [
                { "url": "{{serverUrl}}" }
              ],
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "listItems",
                    "responses": {
                      "200": { "description": "ok" }
                    }
                  }
                }
              }
            }
            """;
    }

    private static MemoryStream CreateGuideZip(string manifestName)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJsonEntry(archive, "manifest.json", new Dictionary<string, object?>
            {
                ["name"] = manifestName,
                ["description"] = "imported",
                ["crew"] = Array.Empty<object>()
            });
            WriteTextEntry(archive, "instructions.md", "Do helpful things.");
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateGuideZipWithAssistants(
        string guideName,
        IReadOnlyList<string> assistantNames,
        IReadOnlyList<string> crewNames)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJsonEntry(archive, "manifest.json", new Dictionary<string, object?>
            {
                ["name"] = guideName,
                ["description"] = "preview",
                ["crew"] = crewNames.Select(name => new Dictionary<string, object?> { ["name"] = name }).ToArray()
            });
            WriteTextEntry(archive, "instructions.md", "Preview import");

            foreach (var assistantName in assistantNames)
            {
                var assistantManifest = JsonSerializer.Serialize(new
                {
                    name = assistantName,
                    description = "embedded",
                    model = (string?)null,
                    tools = Array.Empty<object>()
                });
                WriteTextEntry(archive, $"assistants/{assistantName}/manifest.json", assistantManifest);
                WriteTextEntry(archive, $"assistants/{assistantName}/instructions.md", $"Instructions for {assistantName}");
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateAssistantZip(string name, int? reasoningEffort = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestData = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = "crew member",
                ["model"] = null,
                ["tools"] = Array.Empty<object>()
            };
            if (reasoningEffort.HasValue)
            {
                manifestData["reasoning_effort"] = reasoningEffort.Value;
            }

            var manifest = JsonSerializer.Serialize(manifestData);
            WriteTextEntry(archive, "manifest.json", manifest);
            WriteTextEntry(archive, "instructions.md", "Assist.");
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteJsonEntry(ZipArchive archive, string path, Dictionary<string, object?> data)
    {
        WriteTextEntry(archive, path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
