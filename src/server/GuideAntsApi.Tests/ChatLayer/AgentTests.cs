using System.Reflection;
using AntRunner.Chat;
using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Mapping;

namespace GuideAntsApi.Tests.ChatLayer;

/// <summary>
/// Covers the deterministic, non-network surface of <see cref="Agent"/>:
/// the pure private helpers (reachable via reflection) and the guard clauses
/// that throw when the static service provider is not initialized.
/// The DB- and network-bound paths (Invoke/GetConversationHistory happy paths,
/// BuildCurrentTurnContextAsync, ComputeToolCallCountAsync, attachment loading)
/// are intentionally not exercised here as they require a live SQL database.
/// </summary>
[TestClass]
public sealed class AgentTests
{
    private const string ConnEnvVar = "ConnectionStrings:DefaultConnection";

    private static object? InvokePrivate(string name, params object?[] args)
    {
        var method = typeof(Agent).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException($"Method {name} not found.");
        return method.Invoke(null, args);
    }

    private static bool ToolCallsContainId(string toolCallsJson, string toolCallId)
        => (bool)InvokePrivate("ToolCallsContainId", toolCallsJson, toolCallId)!;

    [TestMethod]
    public void ToolCallsContainId_ReturnsTrue_WhenIdPresentInArray()
    {
        const string json = """[{"id":"call_1"},{"id":"call_2"}]""";

        ToolCallsContainId(json, "call_2").Should().BeTrue();
    }

    [TestMethod]
    public void ToolCallsContainId_ReturnsFalse_WhenIdAbsent()
    {
        const string json = """[{"id":"call_1"}]""";

        ToolCallsContainId(json, "missing").Should().BeFalse();
    }

    [TestMethod]
    public void ToolCallsContainId_ReturnsFalse_ForNonArrayJson()
    {
        const string json = """{"id":"call_1"}""";

        ToolCallsContainId(json, "call_1").Should().BeFalse();
    }

    [TestMethod]
    public void ToolCallsContainId_ReturnsFalse_ForEmptyInputs()
    {
        ToolCallsContainId("", "call_1").Should().BeFalse();
        ToolCallsContainId("""[{"id":"call_1"}]""", "").Should().BeFalse();
    }

    [TestMethod]
    public void ToolCallsContainId_UsesSubstringScan_WhenJsonMalformed()
    {
        // Not valid JSON (parse throws), so the catch-branch substring scan runs.
        const string malformed = """garbage "id":"call_9" garbage""";

        ToolCallsContainId(malformed, "call_9").Should().BeTrue();
        ToolCallsContainId("garbage with no id token", "call_9").Should().BeFalse();
    }

    [TestMethod]
    public void FilterDuplicateAssistantMessages_DropsToollessDuplicateOfToolCallMessage()
    {
        var withTools = new NotebookConversationMessage
        {
            Role = ChatRole.Assistant,
            Content = "Doing work",
            ToolCalls = """[{"id":"call_1"}]""",
            TurnIndex = 0
        };
        var toollessDuplicate = new NotebookConversationMessage
        {
            Role = ChatRole.Assistant,
            Content = "Doing work",
            ToolCalls = null,
            TurnIndex = 0
        };
        var unrelated = new NotebookConversationMessage
        {
            Role = ChatRole.User,
            Content = "Hello",
            TurnIndex = 0
        };

        var input = new List<NotebookConversationMessage> { withTools, toollessDuplicate, unrelated };

        var result = ConversationMessageMapper.FilterDuplicateAssistantMessages(input);

        result.Should().Contain(withTools);
        result.Should().Contain(unrelated);
        result.Should().NotContain(toollessDuplicate);
    }

    [TestMethod]
    public void FilterDuplicateAssistantMessages_KeepsToollessMessage_WhenNoMatchingToolCallContent()
    {
        var toolless = new NotebookConversationMessage
        {
            Role = ChatRole.Assistant,
            Content = "Unique answer",
            ToolCalls = null,
            TurnIndex = 1
        };

        var input = new List<NotebookConversationMessage> { toolless };

        var result = ConversationMessageMapper.FilterDuplicateAssistantMessages(input);

        result.Should().ContainSingle().Which.Should().Be(toolless);
    }

    [TestMethod]
    public void FilterDuplicateAssistantMessages_DistinguishesByTurnIndex()
    {
        // Same content, with-tools on turn 0; toolless copy on turn 1 must be kept.
        var withTools = new NotebookConversationMessage
        {
            Role = ChatRole.Assistant,
            Content = "Shared",
            ToolCalls = """[{"id":"c"}]""",
            TurnIndex = 0
        };
        var toollessOtherTurn = new NotebookConversationMessage
        {
            Role = ChatRole.Assistant,
            Content = "Shared",
            ToolCalls = null,
            TurnIndex = 1
        };

        var input = new List<NotebookConversationMessage> { withTools, toollessOtherTurn };

        var result = ConversationMessageMapper.FilterDuplicateAssistantMessages(input);

        result.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task Invoke_ThrowsWhenServiceProviderNotInitialized()
    {
        Agent.InitializeServiceProvider(null!);
        var context = CreateContext();

        var act = async () => await Agent.Invoke("assistant", "instructions", context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service provider is not initialized*");
    }

    [TestMethod]
    public async Task GetConversationHistory_ThrowsWhenServiceProviderNotInitialized()
    {
        Agent.InitializeServiceProvider(null!);
        var context = CreateContext();

        var act = async () => await Agent.GetConversationHistory(context);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service provider not initialized*");
    }

    /// <summary>
    /// Builds an <see cref="InvocationContext"/> without touching the database. The record's
    /// IsPublished initializer probes the DB via a factory; clearing the connection-string env
    /// var makes that probe fail fast (it is swallowed and returns false).
    /// </summary>
    private static InvocationContext CreateContext()
    {
        var previous = Environment.GetEnvironmentVariable(ConnEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnEnvVar, null);
            return new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnEnvVar, previous);
        }
    }
}
