using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

/// <summary>
/// Regression guard for the GetGuideAsync query decomposition: every query the service issues
/// must translate under the real SQL Server provider. The in-memory provider used by the other
/// GuidesService tests evaluates string.Equals(StringComparison) and string interpolation on
/// the client side, so it cannot catch SQL-translation failures - ToQueryString() with
/// UseSqlServer forces exactly the translation that failed at runtime. No database connection
/// is required: translation happens before any round trip.
/// </summary>
[TestClass]
public sealed class GuidesServiceSqlTranslationTests
{
    private const string SqlServerConnectionString =
        "Server=localhost;Database=guideants-sql-translation-tests;User Id=sa;Password=unused;TrustServerCertificate=True";

    private static ApplicationDbContext CreateSqlServerContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(SqlServerConnectionString)
            .Options;
        return new ApplicationDbContext(options);
    }

    [TestMethod]
    public void GetGuideAsync_queries_translate_under_sql_server_provider()
    {
        Guid guideId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        using var context = CreateSqlServerContext();

        // Guide scalar row (Include Model)
        var guideSql = context.Assistants
            .Include(a => a.Model)
            .Where(a => a.Id == guideId && a.Kind == AssistantKind.Guide)
            .ToQueryString();
        Assert.Contains("[Assistants]", guideSql, StringComparison.OrdinalIgnoreCase);

        // Tools + Tool
        var toolsSql = context.AssistantTools
            .Where(t => t.AssistantId == guideId)
            .Select(t => new ToolAssignmentDto(t.ToolId, t.Tool.ToolType, t.Tool.DisplayName, false))
            .ToQueryString();
        Assert.Contains("[AssistantTools]", toolsSql, StringComparison.OrdinalIgnoreCase);

        // Context options
        var contextOptionsSql = context.AssistantContextOptions
            .Where(co => co.AssistantId == guideId)
            .Select(co => new ContextOptionDto(co.Key, co.Value))
            .ToQueryString();
        Assert.Contains("[AssistantContextOptions]", contextOptionsSql, StringComparison.OrdinalIgnoreCase);

        // OpenAPI schemas + auth provider + scopes + operations
        var schemasSql = context.AssistantOpenApiSchemas
            .Where(s => s.AssistantId == guideId)
            .Select(s => new CustomToolDto(
                s.Name,
                s.SpecificationJson,
                s.ApiHost,
                s.AuthProvider == null ? null : new OpenApiAuthConfigDto(
                    s.AuthProvider!.AuthType,
                    s.AuthProvider.ClientId,
                    s.AuthProvider.Tenant,
                    s.AuthProvider.Scopes.Select(sc => sc.Scope).ToList(),
                    string.IsNullOrEmpty(s.AuthProvider.ValueTemplate) ? null : "••••••••",
                    s.AuthProvider.HeaderName,
                    s.AuthProvider.UserConfigPolicy),
                s.Operations.Select(op => new OpenApiOperationDto(
                    op.Id, op.OperationId, op.Method, op.Path, op.Summary,
                    op.SchemaFragmentJson, op.ToolDefinitionJson)).ToList()))
            .ToQueryString();
        Assert.Contains("[AssistantOpenApiSchemas]", schemasSql, StringComparison.OrdinalIgnoreCase);

        // File metadata (all kinds in one query; FolderKind partition happens client-side
        // because string.Equals(StringComparison) is not SQL-translatable). ContentBytes must
        // NOT appear in this projection.
        var filesSql = context.AssistantFiles
            .Where(f => f.AssistantId == guideId)
            .Select(f => new { f.Id, f.FolderKind, f.VectorStoreName, f.RelativePath, f.ContentType, f.Created })
            .ToQueryString();
        Assert.Contains("[AssistantFiles]", filesSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ContentBytes", filesSql);

        // Manifest bytes (only SKILL.md files)
        var manifestIds = new[] { Guid.NewGuid() };
        var manifestSql = context.AssistantFiles
            .Where(f => manifestIds.Contains(f.Id))
            .Select(f => new { f.Id, f.ContentBytes })
            .ToQueryString();
        Assert.Contains("ContentBytes", manifestSql);

        // Batched markdown-shadow lookup
        var vectorStoreFileIds = new[] { Guid.NewGuid() };
        var shadowSql = context.AssistantFileMarkdownShadows
            .Where(s => vectorStoreFileIds.Contains(s.OriginalAssistantFileId))
            .ToQueryString();
        Assert.Contains("[AssistantFileMarkdownShadows]", shadowSql, StringComparison.OrdinalIgnoreCase);

        // Skill metas
        var skillMetasSql = context.AssistantSkillMetas
            .Where(m => m.AssistantId == guideId)
            .ToQueryString();
        Assert.Contains("[AssistantSkillMetas]", skillMetasSql, StringComparison.OrdinalIgnoreCase);

        // Conversation starters
        var startersSql = context.AssistantConversationStarters
            .Where(cs => cs.AssistantId == guideId)
            .OrderBy(cs => cs.OrderIndex)
            .Select(cs => new ConversationStarterDto(cs.Id, cs.Prompt, cs.OrderIndex))
            .ToQueryString();
        Assert.Contains("[AssistantConversationStarters]", startersSql, StringComparison.OrdinalIgnoreCase);

        // Crew members: the avatar URL is a $"..." interpolation in the projection
        // (translatable in EF Core 6+, same pattern as GetGuidesAsync). The whole projection
        // must translate under the SQL Server provider.
        var crewSql = context.GuideMembers
            .Where(gm => gm.GuideId == guideId)
            .OrderBy(gm => gm.DisplayOrder)
            .Select(gm => new CrewMemberDto(
                gm.AssistantId,
                gm.Assistant.Name,
                gm.Assistant.AvatarImageBytes != null ? $"/api/assistants/{gm.AssistantId}/avatar" : null,
                false,
                gm.DisplayOrder ?? 0,
                gm.MaxToolCallsPerInvocation,
                gm.Assistant.MaxToolCallsPerTurn))
            .ToQueryString();
        Assert.Contains("[GuideMembers]", crewSql, StringComparison.OrdinalIgnoreCase);
    }
}
