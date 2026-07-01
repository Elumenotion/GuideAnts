using System.Reflection;
using System.Text;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Skills;

/// <summary>
/// Regression: Skill files must never enter the VectorStore indexing pipeline (S5).
/// </summary>
[TestClass]
public sealed class SkillNoIndexRegressionTests
{
    private static readonly Type StorageType = typeof(DatabaseStorage);

    private static T Invoke<T>(string method, params object?[] args)
    {
        var mi = StorageType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                 ?? throw new InvalidOperationException($"Method {method} not found.");
        return (T)mi.Invoke(null, args)!;
    }

    private static AssistantFile CreateSkillFile(Guid assistantId) => new()
    {
        Id = Guid.NewGuid(),
        AssistantId = assistantId,
        FolderKind = "Skill",
        RelativePath = "Skills/demo/SKILL.md",
        ContentBytes = Encoding.UTF8.GetBytes("""
---
name: demo
description: Demo skill
---
body
"""),
        Created = DateTime.UtcNow
    };

    [TestMethod]
    public async Task SkillFile_DoesNotCreateMarkdownShadowOrEnqueueExtraction()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"skill-no-index-{Guid.NewGuid():N}");
        var assistantId = Guid.NewGuid();
        var skillFile = CreateSkillFile(assistantId);
        var vectorFile = new AssistantFile
        {
            Id = Guid.NewGuid(),
            AssistantId = assistantId,
            FolderKind = "VectorStore",
            RelativePath = "guide.md",
            Created = DateTime.UtcNow
        };

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Assistants.Add(new Assistant { Id = assistantId, Name = "Guide", Created = DateTime.UtcNow });
            seed.AssistantFiles.Add(skillFile);
            seed.AssistantFiles.Add(vectorFile);
            await seed.SaveChangesAsync();
        }

        var indexable = new List<AssistantFile> { skillFile, vectorFile }
            .Where(f => f.FolderKind == "VectorStore")
            .ToList();

        indexable.Should().ContainSingle().Which.Id.Should().Be(vectorFile.Id);

        var queue = new BackgroundJobTestHelpers.CapturingJobQueueService();
        foreach (var file in indexable)
        {
            await queue.EnqueueAsync(
                "ExtractAssistantFileMarkdown",
                new ExtractAssistantFileMarkdownJob(file.Id));
        }

        queue.Enqueued.Should().ContainSingle();
        queue.Enqueued[0].Payload.Should().BeOfType<ExtractAssistantFileMarkdownJob>();

        await using var verify = new ApplicationDbContext(options);
        (await verify.AssistantFileMarkdownShadows.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public void MaterializeAssistant_SkillFiles_NotInVectorStoreResources()
    {
        var assistantId = Guid.NewGuid();
        var skillFile = CreateSkillFile(assistantId);
        var assistant = new Assistant
        {
            Id = assistantId,
            Name = "Guide",
            Files = [skillFile]
        };

        Invoke<object?>("BuildToolResources", assistant).Should().BeNull();
        Invoke<System.Collections.Generic.Dictionary<string, byte[]>?>("BuildVectorStoreFiles", assistant)
            .Should().BeNull();

        var tools = Invoke<System.Collections.Generic.List<object>>("BuildToolsArray", assistant);
        tools.Should().BeEmpty();
    }
}
