using System.Reflection;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Endpoints.PublishedWire;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.Mcp;
using GuideAntsApi.Services.PublishedWireApi;
using Moq;

namespace GuideAntsApi.Tests.Services.Mcp;

/// <summary>
/// Phase 7 cross-cutting acceptance: one executor (E3/E15) on notebook, embed, and wire.
/// </summary>
[TestClass]
public sealed class McpRuntimeParityAcceptanceTests
{
    private const string LegacySandboxPackageSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "MCP", "version": "1.0.0" },
          "servers": [{ "url": "client://mcp-bridge-pkg" }],
          "x-guideants-tool-source": {
            "kind": "mcp",
            "transport": "client_bridge",
            "bridgeId": "pkg",
            "toolNamePrefix": "mcp_github",
            "package": {
              "registryType": "npm",
              "identifier": "@example/mcp-server",
              "command": "npx",
              "args": ["-y", "@example/mcp-server"]
            },
            "environmentVariables": [
              { "name": "EXAMPLE_API_KEY", "secretRef": "{{secret:EXAMPLE_API_KEY}}" }
            ]
          },
          "paths": {
            "/tools/run": { "post": { "operationId": "run", "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

    [TestMethod]
    public void ThreadRun_exposes_mcp_bridge_entrypoints_for_api_and_sandbox()
    {
        var threadRunType = typeof(AntRunner.Chat.ThreadRun);
        var methods = threadRunType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        methods.Should().Contain("ExecuteMcpApiToolStaticAsync");
        methods.Should().Contain("ExecuteMcpSandboxToolStaticAsync");

        var source = ReadRepoFile("src", "server", "AntRunner.Chat", "AntRunner.Chat", "ThreadRun.cs");
        source.Should().Contain("ActionType.McpApi");
        source.Should().Contain("ActionType.McpSandbox");
        source.Should().Contain("McpToolExecutionBridge");
        source.Should().NotContain("client://mcp-bridge");
    }

    [TestMethod]
    public void McpToolExecutionBridge_routes_api_and_sandbox_to_IMcpToolExecutor()
    {
        var bridgeType = typeof(McpToolExecutionBridge);
        bridgeType.GetMethod("ExecuteMcpApiTool").Should().NotBeNull();
        bridgeType.GetMethod("ExecuteMcpSandboxTool").Should().NotBeNull();

        var executorType = typeof(McpToolExecutor);
        executorType.GetMethod("ExecuteApiToolAsync").Should().NotBeNull();
        executorType.GetMethod("ExecuteSandboxToolAsync").Should().NotBeNull();
        executorType.GetInterfaces().Should().Contain(i => i == typeof(IMcpToolExecutor));
    }

    [TestMethod]
    public void Notebook_stream_engine_delegates_tool_execution_to_ThreadRun()
    {
        var source = ReadRepoFile(
            "src", "server", "GuideAntsApi", "Services", "Conversations", "Streaming", "ConversationStreamEngine.cs");
        source.Should().Contain("ThreadRun.ExecuteAsync");
    }

    [TestMethod]
    public async Task Wire_path_uses_SendMessageStreamAsync_as_single_engine_entry()
    {
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var conversationService = new Mock<IPublishedConversationService>();
        conversationService
            .Setup(s => s.CreateConversationAsync(notebookId, WireConversationExecutor.DefaultConversationTitle))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.Token, "{\"contentDelta\":\"ok\"}")));

        var pubId = Guid.NewGuid();
        var context = new PublishedApiExecutionContext(
            PubId: pubId,
            ProjectId: Guid.NewGuid(),
            NotebookId: notebookId,
            GuideId: Guid.NewGuid(),
            PublishedGuide: new PublishedGuide { Id = pubId, Active = true },
            WireApiConfig: new PublishedWireApiConfigDto { Enabled = true },
            AuthMode: PublishedApiAuthMode.Anonymous,
            ExternalUserIdentity: "wire-user",
            InternalUserId: null,
            SourceChannel: PublishedApiExecutionContextResolver.WireApiSourceChannel,
            ExternalRequestId: "req-test",
            EndpointName: "chat.completions");

        var handle = await WireConversationExecutor.StartConversationStreamAsync(
            conversationService.Object,
            context,
            instructions: "hello",
            ct: CancellationToken.None);

        handle.ConversationId.Should().Be(conversationId);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                context.PubId.ToString(),
                context.ExternalUserIdentity,
                context.InternalUserId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public void Embed_invoke_path_uses_SendMessageStreamAsync_and_shared_wire_fold()
    {
        var source = ReadRepoFile("src", "server", "GuideAntsApi", "Endpoints", "PublishedGuidesEndpoints.cs");
        source.Should().Contain("SendMessageStreamAsync");
        source.Should().Contain("WireConversationExecutor.CollectWireConversationResultAsync");
    }

    [TestMethod]
    public void Published_conversation_service_delegates_tool_calls_to_ThreadRun_DoToolCalls()
    {
        var source = ReadRepoFile(
            "src", "server", "GuideAntsApi", "Services", "Conversations", "PublishedConversationService.cs");
        source.Should().Contain("ThreadRun.DoToolCalls");
    }

    [TestMethod]
    public void NormalizeDescriptor_Migrates_legacy_sandbox_package_round_trip()
    {
        var normalized = ToolSourceValidator.NormalizeDescriptor(LegacySandboxPackageSpec);
        using var doc = System.Text.Json.JsonDocument.Parse(normalized);

        doc.RootElement.GetProperty("servers")[0].GetProperty("url").GetString()
            .Should().Be("mcp+sandbox://pkg");
        normalized.Should().Contain("\"runtimeExecution\": \"sandbox_subprocess\"");
        normalized.Should().Contain("\"discoveryTransport\": \"stdio\"");
        normalized.Should().Contain("@example/mcp-server");
        normalized.Should().Contain("{{secret:EXAMPLE_API_KEY}}");
        normalized.Should().NotContain("client://mcp-bridge");

        McpDescriptorMigrator.NeedsMigration(normalized).Should().BeFalse();
        ToolSourceValidator.ValidateDescriptor(normalized, publishChecks: false)
            .Should().NotContain(m => m.Code == "legacy_mcp_transport");
    }

    [TestMethod]
    public void ToolCaller_dispatch_has_no_client_mcp_bridge_scheme()
    {
        var source = ReadRepoFile(
            "src", "server", "AntRunner.Chat", "AntRunner.ToolCalling", "Functions", "ToolCaller.cs");
        source.Should().Contain("mcp+api");
        source.Should().Contain("mcp+sandbox");
        source.Should().NotContain("mcp-bridge");
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repo file: {string.Join('/', segments)}");
    }

    private static async IAsyncEnumerable<StreamingEvent> StreamEvents(params StreamingEvent[] events)
    {
        foreach (var ev in events)
        {
            yield return ev;
            await Task.Yield();
        }
    }
}
