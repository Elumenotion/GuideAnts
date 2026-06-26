using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.Endpoints.PublishedWire;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Endpoints;

/// <summary>
/// Regression coverage for transcript-based conversation continuation when a client
/// (e.g. Codex/Cursor) replays a prefix that GuideAnts never persists, such as the
/// leading <c>&lt;environment_context&gt;</c> user message. The client transcript is
/// therefore longer than the persisted conversation, and continuation must still match.
/// </summary>
[TestClass]
public sealed class PublishedWireTranscriptMatchingTests
{
    private const string EnvironmentContext =
        "<environment_context>\n  <cwd>D:\\repos\\GuideAnts</cwd>\n  <shell>powershell</shell>\n</environment_context>";
    private const string FirstUserPrompt = "This is the first message in the turn. Reply with 'Uno'";
    private const string AssistantReply = "Uno";

    private static WireConversationResolver.PersistedHistoryRow UserRow(string content) =>
        new(DataModelChatRole.User, content, null, null);

    private static WireConversationResolver.PersistedHistoryRow AssistantRow(string content) =>
        new(DataModelChatRole.Assistant, content, null, null);

    [TestMethod]
    public void TranscriptEndsWith_matches_when_client_replays_unpersisted_environment_context_prefix()
    {
        // Client replay (Codex /responses input, prefix only — current prompt already peeled off):
        // a leading unpersisted <environment_context> user message, then the persisted turn.
        var clientPrefix = new List<ChatMessage>
        {
            new(ChatMessageRole.User, EnvironmentContext),
            new(ChatMessageRole.User, FirstUserPrompt),
            new(ChatMessageRole.Assistant, AssistantReply)
        };
        var expectedHistory = WireConversationResolver.BuildWireTranscriptHistory(clientPrefix);

        // What GuideAnts actually persisted: just the real user turn and assistant reply.
        var persistedHistory = WireConversationResolver.BuildPersistedTranscriptHistory(
        [
            UserRow(FirstUserPrompt),
            AssistantRow(AssistantReply)
        ]);

        // Persisted history is shorter than the replayed transcript purely because of the
        // client-injected prefix; this must still be recognized as a continuation.
        persistedHistory.Count.Should().BeLessThan(expectedHistory.Count);
        WireConversationResolver.TranscriptEndsWith(persistedHistory, expectedHistory)
            .Should().BeTrue();
    }

    [TestMethod]
    public void TranscriptEndsWith_does_not_match_a_different_conversation_tail()
    {
        var clientPrefix = new List<ChatMessage>
        {
            new(ChatMessageRole.User, EnvironmentContext),
            new(ChatMessageRole.User, FirstUserPrompt),
            new(ChatMessageRole.Assistant, AssistantReply)
        };
        var expectedHistory = WireConversationResolver.BuildWireTranscriptHistory(clientPrefix);

        var persistedHistory = WireConversationResolver.BuildPersistedTranscriptHistory(
        [
            UserRow("a completely different question"),
            AssistantRow("a completely different answer")
        ]);

        WireConversationResolver.TranscriptEndsWith(persistedHistory, expectedHistory)
            .Should().BeFalse();
    }

    [TestMethod]
    public void TranscriptEndsWith_matches_multi_turn_replay_with_repeated_prefix()
    {
        // Third turn: client replays the full prior exchange plus repeated scaffolding prefix.
        var clientPrefix = new List<ChatMessage>
        {
            new(ChatMessageRole.User, EnvironmentContext),
            new(ChatMessageRole.User, EnvironmentContext),
            new(ChatMessageRole.User, FirstUserPrompt),
            new(ChatMessageRole.Assistant, AssistantReply),
            new(ChatMessageRole.User, "and next is?"),
            new(ChatMessageRole.Assistant, "Dos")
        };
        var expectedHistory = WireConversationResolver.BuildWireTranscriptHistory(clientPrefix);

        var persistedHistory = WireConversationResolver.BuildPersistedTranscriptHistory(
        [
            UserRow(FirstUserPrompt),
            AssistantRow(AssistantReply),
            UserRow("and next is?"),
            AssistantRow("Dos")
        ]);

        WireConversationResolver.TranscriptEndsWith(persistedHistory, expectedHistory)
            .Should().BeTrue();
    }
}
