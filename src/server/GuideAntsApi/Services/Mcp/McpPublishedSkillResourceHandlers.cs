using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Skills;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GuideAntsApi.Services.Mcp;

public static class McpPublishedSkillResourceHandlers
{
    public static async ValueTask<ListResourcesResult> ListResourcesAsync(
        RequestContext<ListResourcesRequestParams> request,
        CancellationToken cancellationToken)
    {
        var mcpContext = request.Services?.GetService(typeof(McpPublishedGuideContext)) as McpPublishedGuideContext;
        if (mcpContext is not { IsValid: true })
        {
            return new ListResourcesResult { Resources = [] };
        }

        var entries = await PublishedSkillCatalog.ListVisibleSkillsAsync(
            mcpContext.GuideName,
            mcpContext.AddressableAssistants,
            cancellationToken);

        var resources = entries.Select(entry => new Resource
        {
            Uri = entry.PublishedLocator,
            Name = entry.Descriptor.Name,
            Description = entry.Descriptor.Description,
            MimeType = "text/markdown"
        }).ToList();

        return new ListResourcesResult { Resources = resources };
    }

    public static async ValueTask<ReadResourceResult> ReadResourceAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken)
    {
        var mcpContext = request.Services?.GetService(typeof(McpPublishedGuideContext)) as McpPublishedGuideContext;
        if (mcpContext is not { IsValid: true })
        {
            throw new McpException("MCP context is not valid.");
        }

        var uri = request.Params?.Uri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new McpException("Resource URI is required.");
        }

        if (!SkillLocator.TryParse(uri, out var parts) || !parts.IsPublished)
        {
            throw new McpException($"Unsupported skill resource URI '{uri}'.");
        }

        if (!string.Equals(parts.GuideName, mcpContext.GuideName, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                $"Skill resource '{uri}' is not in scope for this published guide.");
        }

        var entry = await PublishedSkillCatalog.FindSkillForReadAsync(
            mcpContext.GuideName,
            mcpContext.AddressableAssistants,
            parts.SkillName,
            cancellationToken);

        if (entry == null)
        {
            throw new McpException(
                $"Unknown skill '{parts.SkillName}' for guide '{mcpContext.GuideName}'.");
        }

        var db = request.Services!.GetRequiredService<ApplicationDbContext>();
        string? filePath = parts.ReferenceRelativePath;
        if (!string.IsNullOrWhiteSpace(filePath)
            && !filePath.StartsWith("references/", StringComparison.OrdinalIgnoreCase))
        {
            filePath = $"references/{filePath}";
        }

        var read = await SkillContentReader.TryReadAsync(
            db,
            entry.AssistantId,
            entry.Descriptor,
            filePath,
            cancellationToken);

        if (read == null)
        {
            throw new McpException(
                $"Skill file for '{uri}' could not be read safely.");
        }

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "text/markdown",
                    Text = read.Content
                }
            ]
        };
    }
}
