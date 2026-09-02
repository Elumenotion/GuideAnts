using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.UserProjectContextOptions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ContextOptionsServiceDeepTests
{
    [TestMethod]
    public async Task ResolveAsync_Resolves_files_and_user_override_branches()
    {
        await using var db = CreateDbContext();
        var notebookId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var outputFile = new NotebookFile
        {
            NotebookId = notebookId,
            RelativePath = "Output/report.csv",
            FileSize = 128,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash-output"
        };
        outputFile.GenerateDocumentId(notebookId);
        var docsFile = new NotebookFile
        {
            NotebookId = notebookId,
            RelativePath = "docs/spec.md",
            FileSize = 256,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash-docs"
        };
        docsFile.GenerateDocumentId(notebookId);
        db.NotebookFiles.AddRange(outputFile, docsFile);
        await db.SaveChangesAsync();

        var currentUser = new CurrentUserContext(
            Guid.NewGuid(),
            "Casey",
            "casey@example.com",
            Role.Admin,
            false,
            Guid.NewGuid(),
            null);

        var service = CreateService(
            db,
            currentUser,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = "project-override@example.com"
            });

        var assistant = new AssistantDefinition
        {
            ContextOptions = new Dictionary<string, string>
            {
                ["files"] = "[@files]",
                ["email"] = "[@userEmail]",
                ["unknown"] = "[@notARealToken]"
            }
        };

        var resolved = await service.ResolveAsync(assistant, projectId, notebookId, Guid.NewGuid());

        resolved["email"].Should().Be("project-override@example.com");
        resolved["unknown"].Should().Be("[@notARealToken]");
        resolved["files"].Should().Contain("```console");
        resolved["files"].Should().Contain("report.csv");
        resolved["files"].Should().Contain("../docs/spec.md");
    }

    [TestMethod]
    public async Task BuildContextMessageAsync_Converts_blank_values_to_missing_unknown()
    {
        await using var db = CreateDbContext();
        var service = CreateService(
            db,
            currentUser: null,
            userProjectOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var assistant = new AssistantDefinition
        {
            ContextOptions = new Dictionary<string, string>
            {
                ["empty"] = string.Empty,
                ["user"] = "[@userName]"
            }
        };

        var message = await service.BuildContextMessageAsync(assistant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var contextOptions = ParseContextOptions(message);
        contextOptions["empty"].Should().Be("MISSING/UNKNOWN");
        contextOptions["user"].Should().Be("MISSING/UNKNOWN");
    }

    [TestMethod]
    public async Task BuildPublishedContextMessage_Omits_unsupported_tokens_and_resolves_files()
    {
        await using var db = CreateDbContext();
        var notebookId = Guid.NewGuid();

        var image = new NotebookFile
        {
            NotebookId = notebookId,
            RelativePath = "Output/figure.png",
            FileSize = 777,
            LastModifiedUtc = DateTime.UtcNow,
            FileHash = "hash-figure"
        };
        image.GenerateDocumentId(notebookId);
        db.NotebookFiles.Add(image);
        await db.SaveChangesAsync();

        var service = CreateService(
            db,
            currentUser: null,
            userProjectOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var assistant = new AssistantDefinition
        {
            ContextOptions = new Dictionary<string, string>
            {
                ["files"] = "[@files]",
                ["today"] = "[@currentDate]",
                ["literal"] = "always-keep",
                ["user"] = "[@userName]",
                ["unknown"] = "[@missingToken]"
            }
        };

        var message = await service.BuildPublishedContextMessageAsync(assistant, Guid.NewGuid(), notebookId);

        var contextOptions = ParseContextOptions(message);
        contextOptions.Should().ContainKey("files");
        contextOptions["files"].Should().Contain("../../Output/figure.png");
        contextOptions.Should().ContainKey("today");
        contextOptions["today"].Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
        contextOptions["literal"].Should().Be("always-keep");
        contextOptions.Should().NotContainKey("user");
        contextOptions.Should().NotContainKey("unknown");
    }

    [TestMethod]
    public async Task BuildPublishedContextMessage_Returns_null_when_all_options_are_omitted()
    {
        await using var db = CreateDbContext();
        var service = CreateService(
            db,
            currentUser: null,
            userProjectOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var assistant = new AssistantDefinition
        {
            ContextOptions = new Dictionary<string, string>
            {
                ["user"] = "[@userName]",
                ["unknown"] = "[@doesNotExist]"
            }
        };

        var message = await service.BuildPublishedContextMessageAsync(assistant, Guid.NewGuid(), Guid.NewGuid());

        message.Should().BeNull();
    }

    private static ContextOptionsService CreateService(
        ApplicationDbContext db,
        CurrentUserContext? currentUser,
        Dictionary<string, string> userProjectOptions)
    {
        var currentUserService = new Mock<ICurrentUserService>();
        currentUserService
            .Setup(x => x.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentUser);

        var userOptionsService = new Mock<IUserProjectContextOptionsService>();
        userOptionsService
            .Setup(x => x.GetOptionsAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(userProjectOptions);

        return new ContextOptionsService(
            db,
            currentUserService.Object,
            userOptionsService.Object,
            new LegacyStoragePathResolver(Path.Combine(Path.GetTempPath(), "context-options-missing-" + Guid.NewGuid().ToString("N"))));
    }

    private static Dictionary<string, string> ParseContextOptions(string? message)
    {
        message.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(message!);
        return doc.RootElement.GetProperty("contextOptions")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"context-options-deep-{Guid.NewGuid():N}")
            .Options;

        return new ApplicationDbContext(options);
    }
}
