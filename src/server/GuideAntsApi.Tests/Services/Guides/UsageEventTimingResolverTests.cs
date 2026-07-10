using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class UsageEventTimingResolverTests
{
    [TestMethod]
    public async Task Resolve_ToolCall_UsesAssistantRequestAndToolResultMessagePair()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"usage-timing-{Guid.NewGuid():N}")
            .Options;

        var conversationId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var toolMessageId = Guid.NewGuid();
        var assistantStart = new DateTime(2026, 7, 10, 20, 0, 0, DateTimeKind.Utc);
        var toolEnd = assistantStart.AddSeconds(8);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.AgentInvocationMessages.Add(new AgentInvocationMessage
            {
                Id = Guid.NewGuid(),
                AgentInvocationId = invocationId,
                Sequence = 0,
                Role = ChatRole.Assistant,
                Content = "call tool",
                ToolCallsJson = """[{"id":"call_123","function":{"name":"generate_podcast"}}]""",
                Created = assistantStart,
            });
            seed.AgentInvocationMessages.Add(new AgentInvocationMessage
            {
                Id = Guid.NewGuid(),
                AgentInvocationId = invocationId,
                Sequence = 1,
                Role = ChatRole.Tool,
                Content = "done",
                FunctionName = "generate_podcast",
                ToolCallId = "call_123",
                Created = toolEnd,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var resolver = await UsageEventTimingResolver.CreateAsync(
            context,
            conversationId,
            [invocationId]);

        var usageEvent = new UsageEvent
        {
            Id = Guid.NewGuid(),
            Category = UsageCategory.ToolCall,
            Operation = "generate_podcast",
            Created = toolEnd.AddMinutes(5),
            AgentInvocationId = invocationId,
            MetadataJson = """{"toolCallId":"call_123"}""",
        };

        var timing = resolver.Resolve(usageEvent);

        timing.Start.Should().Be(assistantStart);
        timing.End.Should().Be(toolEnd);
        timing.DurationMs.Should().Be(8_000);
    }

    [TestMethod]
    public async Task Resolve_ToolCall_UsesLinkedToolResultMessageId()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"usage-timing-linked-{Guid.NewGuid():N}")
            .Options;

        var conversationId = Guid.NewGuid();
        var toolMessageId = Guid.NewGuid();
        var assistantStart = new DateTime(2026, 7, 10, 20, 0, 0, DateTimeKind.Utc);
        var toolEnd = assistantStart.AddSeconds(5);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                Role = ChatRole.Assistant,
                Content = "call tool",
                ToolCalls = """[{"id":"call_abc","function":{"name":"run_bash"}}]""",
                TurnIndex = 0,
                MessageSequence = 0,
                Created = assistantStart,
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = toolMessageId,
                NotebookConversationId = conversationId,
                Role = ChatRole.Tool,
                Content = "ok",
                FunctionName = "run_bash",
                ToolCallId = "call_abc",
                TurnIndex = 0,
                MessageSequence = 1,
                Created = toolEnd,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var resolver = await UsageEventTimingResolver.CreateAsync(context, conversationId, []);

        var usageEvent = new UsageEvent
        {
            Id = Guid.NewGuid(),
            Category = UsageCategory.ToolCall,
            Operation = "run_bash",
            Created = toolEnd.AddHours(1),
            NotebookConversationMessageId = toolMessageId,
        };

        var timing = resolver.Resolve(usageEvent);

        timing.Start.Should().Be(assistantStart);
        timing.End.Should().Be(toolEnd);
        timing.DurationMs.Should().Be(5_000);
    }

    [TestMethod]
    public async Task Resolve_ImageGeneration_UsesGenerateImageMessagePair()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"usage-timing-image-{Guid.NewGuid():N}")
            .Options;

        var conversationId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var assistantStart = new DateTime(2026, 7, 10, 20, 0, 0, DateTimeKind.Utc);
        var toolEnd = assistantStart.AddSeconds(12);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.AgentInvocationMessages.Add(new AgentInvocationMessage
            {
                Id = Guid.NewGuid(),
                AgentInvocationId = invocationId,
                Sequence = 0,
                Role = ChatRole.Assistant,
                Content = "call tool",
                ToolCallsJson = """[{"id":"call_img","function":{"name":"generate_image"}}]""",
                Created = assistantStart,
            });
            seed.AgentInvocationMessages.Add(new AgentInvocationMessage
            {
                Id = Guid.NewGuid(),
                AgentInvocationId = invocationId,
                Sequence = 1,
                Role = ChatRole.Tool,
                Content = "image saved",
                FunctionName = "generate_image",
                ToolCallId = "call_img",
                Created = toolEnd,
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var resolver = await UsageEventTimingResolver.CreateAsync(
            context,
            conversationId,
            [invocationId]);

        var usageEvent = new UsageEvent
        {
            Id = Guid.NewGuid(),
            Category = UsageCategory.ImageGeneration,
            Operation = "image-generation",
            Created = toolEnd.AddMilliseconds(50),
            AgentInvocationId = invocationId,
        };

        var timing = resolver.Resolve(usageEvent);

        timing.Start.Should().Be(assistantStart);
        timing.End.Should().Be(toolEnd);
        timing.DurationMs.Should().Be(12_000);
    }
}
