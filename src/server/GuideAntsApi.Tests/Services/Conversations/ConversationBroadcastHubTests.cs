using System.Reflection;
using FluentAssertions;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationBroadcastHubTests
{
    [TestMethod]
    public async Task SubscribeToConversationAsync_Emits_connection_event_and_updates_count()
    {
        using var hub = CreateHub();
        var conversationId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var stream = hub.SubscribeToConversationAsync(conversationId, "conn-1", cts.Token);
        await using var enumerator = stream.GetAsyncEnumerator(cts.Token);

        var firstEvent = await ReadNextAsync(enumerator);

        firstEvent.EventType.Should().Be(StreamingEventTypes.ConnectionEstablished);
        firstEvent.Payload.Should().Contain("conn-1");
        hub.GetSubscriberCount(conversationId).Should().Be(1);
        hub.HasActiveSubscribers(conversationId).Should().BeTrue();
    }

    [TestMethod]
    public async Task BroadcastToConversationAsync_Delivers_event_to_active_subscriber()
    {
        using var hub = CreateHub();
        var conversationId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var stream = hub.SubscribeToConversationAsync(conversationId, "conn-2", cts.Token);
        await using var enumerator = stream.GetAsyncEnumerator(cts.Token);
        _ = await ReadNextAsync(enumerator); // connection_established

        var expected = new StreamingEvent(StreamingEventTypes.Token, "{\"delta\":\"hello\"}");
        await hub.BroadcastToConversationAsync(conversationId, expected);

        var broadcastEvent = await ReadNextAsync(enumerator);
        broadcastEvent.Should().BeEquivalentTo(expected);
    }

    [TestMethod]
    public async Task BroadcastToConversationAsync_Removes_subscriber_when_send_fails()
    {
        using var hub = CreateHub();
        var conversationId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var connectionId = "conn-fail";

        var stream = hub.SubscribeToConversationAsync(conversationId, connectionId, cts.Token);
        await using var enumerator = stream.GetAsyncEnumerator(cts.Token);
        _ = await ReadNextAsync(enumerator); // connection_established

        var userSubscription = GetUserSubscription(hub, conversationId, connectionId);
        userSubscription.GetType().GetMethod("Dispose")!.Invoke(userSubscription, null);

        await hub.BroadcastToConversationAsync(conversationId, new StreamingEvent("token", "{\"delta\":\"x\"}"));

        hub.GetSubscriberCount(conversationId).Should().Be(0);
        hub.HasActiveSubscribers(conversationId).Should().BeFalse();
    }

    [TestMethod]
    public async Task CleanupInactiveConversationsAsync_Removes_empty_conversation_entries()
    {
        using var hub = CreateHub();
        var conversationId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var stream = hub.SubscribeToConversationAsync(conversationId, "conn-cleanup", cts.Token);
        await using (var enumerator = stream.GetAsyncEnumerator(cts.Token))
        {
            _ = await ReadNextAsync(enumerator); // connection_established
        }

        DictionaryContainsConversation(hub, conversationId).Should().BeTrue();

        await InvokeCleanupAsync(hub);

        DictionaryContainsConversation(hub, conversationId).Should().BeFalse();
    }

    private static ConversationBroadcastHub CreateHub()
        => new(NullLogger<ConversationBroadcastHub>.Instance);

    private static async Task<StreamingEvent> ReadNextAsync(IAsyncEnumerator<StreamingEvent> enumerator)
    {
        var moveTask = enumerator.MoveNextAsync().AsTask();
        var completed = await Task.WhenAny(moveTask, Task.Delay(TimeSpan.FromSeconds(3)));
        completed.Should().Be(moveTask, "stream event should arrive promptly");
        moveTask.Result.Should().BeTrue();
        return enumerator.Current;
    }

    private static async Task InvokeCleanupAsync(ConversationBroadcastHub hub)
    {
        var method = typeof(ConversationBroadcastHub)
            .GetMethod("CleanupInactiveConversationsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(hub, null)!;
        await task;
    }

    private static object GetUserSubscription(ConversationBroadcastHub hub, Guid conversationId, string connectionId)
    {
        var conversationSubscribers = GetConversationSubscribers(hub, conversationId);
        var subscribersField = conversationSubscribers.GetType()
            .GetField("_subscribers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var subscribersDict = subscribersField.GetValue(conversationSubscribers)!;

        var tryGetValue = subscribersDict.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { connectionId, null };
        var found = (bool)tryGetValue.Invoke(subscribersDict, args)!;
        found.Should().BeTrue("test setup should register the subscriber");
        return args[1]!;
    }

    private static bool DictionaryContainsConversation(ConversationBroadcastHub hub, Guid conversationId)
    {
        var dictionary = GetConversationDictionary(hub);
        var containsKey = dictionary.GetType().GetMethod("ContainsKey")!;
        return (bool)containsKey.Invoke(dictionary, new object[] { conversationId })!;
    }

    private static object GetConversationSubscribers(ConversationBroadcastHub hub, Guid conversationId)
    {
        var dictionary = GetConversationDictionary(hub);
        var tryGetValue = dictionary.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { conversationId, null };
        var found = (bool)tryGetValue.Invoke(dictionary, args)!;
        found.Should().BeTrue("conversation should be registered");
        return args[1]!;
    }

    private static object GetConversationDictionary(ConversationBroadcastHub hub)
    {
        var field = typeof(ConversationBroadcastHub)
            .GetField("_conversationSubscribers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return field.GetValue(hub)!;
    }
}
