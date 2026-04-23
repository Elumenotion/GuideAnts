using System.Collections.Concurrent;
using System.Threading.Channels;
using GuideAntsApi.Models.Conversations;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Manages multi-user subscriptions to conversations and broadcasts streaming events
/// to all active subscribers with efficient memory management and cleanup.
/// </summary>
public interface IConversationBroadcastHub
{
    /// <summary>
    /// Subscribe to real-time events for a specific conversation.
    /// Returns an async enumerable of streaming events.
    /// </summary>
    IAsyncEnumerable<StreamingEvent> SubscribeToConversationAsync(Guid conversationId, string connectionId, CancellationToken cancellationToken);
    
    /// <summary>
    /// Broadcast a streaming event to all active subscribers of a conversation.
    /// </summary>
    Task BroadcastToConversationAsync(Guid conversationId, StreamingEvent streamingEvent);
    
    /// <summary>
    /// Get the count of active subscribers for a conversation.
    /// </summary>
    int GetSubscriberCount(Guid conversationId);
    
    /// <summary>
    /// Check if a conversation has any active subscribers.
    /// </summary>
    bool HasActiveSubscribers(Guid conversationId);
}

public class ConversationBroadcastHub : IConversationBroadcastHub, IDisposable
{
    private readonly ILogger<ConversationBroadcastHub> _logger;
    private readonly ConcurrentDictionary<Guid, ConversationSubscribers> _conversationSubscribers = new();
    private readonly SemaphoreSlim _cleanupSemaphore = new(1, 1);
    private readonly Timer _cleanupTimer;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ConversationBroadcastHub(ILogger<ConversationBroadcastHub> logger)
    {
        _logger = logger;
        
        // Cleanup inactive conversations every 5 minutes
        _cleanupTimer = new Timer(async _ => await CleanupInactiveConversationsAsync(), 
            null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public IAsyncEnumerable<StreamingEvent> SubscribeToConversationAsync(Guid conversationId, string connectionId, CancellationToken cancellationToken)
    {
        var subscribers = _conversationSubscribers.GetOrAdd(conversationId, _ => new ConversationSubscribers(_logger));
        
        _logger.LogInformation("Connection {ConnectionId} subscribing to conversation {ConversationId}", connectionId, conversationId);
        
        return subscribers.Subscribe(connectionId, cancellationToken);
    }

    public async Task BroadcastToConversationAsync(Guid conversationId, StreamingEvent streamingEvent)
    {
        //Console.WriteLine($"🔥 Broadcasting {streamingEvent.EventType} to conversation {conversationId}");
        
        if (_conversationSubscribers.TryGetValue(conversationId, out var subscribers))
        {
            //Console.WriteLine($"📡 Found {subscribers.SubscriberCount} subscribers for conversation {conversationId}");
            await subscribers.BroadcastAsync(streamingEvent);
        }
        else
        {
            //Console.WriteLine($"❌ No subscribers found for conversation {conversationId}");
        }
    }

    public int GetSubscriberCount(Guid conversationId)
    {
        return _conversationSubscribers.TryGetValue(conversationId, out var subscribers) 
            ? subscribers.SubscriberCount 
            : 0;
    }

    public bool HasActiveSubscribers(Guid conversationId)
    {
        return GetSubscriberCount(conversationId) > 0;
    }

    private async Task CleanupInactiveConversationsAsync()
    {
        if (!await _cleanupSemaphore.WaitAsync(100)) return;
        
        try
        {
            var inactiveConversations = new List<Guid>();
            
            foreach (var kvp in _conversationSubscribers)
            {
                if (kvp.Value.SubscriberCount == 0)
                {
                    inactiveConversations.Add(kvp.Key);
                }
            }
            
            foreach (var conversationId in inactiveConversations)
            {
                if (_conversationSubscribers.TryRemove(conversationId, out var subscribers))
                {
                    subscribers.Dispose();
                    _logger.LogDebug("Cleaned up inactive conversation {ConversationId}", conversationId);
                }
            }
            
            if (inactiveConversations.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} inactive conversations", inactiveConversations.Count);
            }
        }
        finally
        {
            _cleanupSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        
        foreach (var kvp in _conversationSubscribers)
        {
            kvp.Value.Dispose();
        }
        
        _conversationSubscribers.Clear();
        _cleanupSemaphore.Dispose();
    }
}

/// <summary>
/// Manages subscribers for a single conversation with efficient broadcasting and cleanup.
/// </summary>
internal class ConversationSubscribers : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, UserSubscription> _subscribers = new();
    private readonly object _broadcastLock = new object();

    public ConversationSubscribers(ILogger logger)
    {
        _logger = logger;
    }

    public int SubscriberCount => _subscribers.Count;

    public async IAsyncEnumerable<StreamingEvent> Subscribe(string connectionId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var subscription = new UserSubscription(connectionId, cancellationToken);
        _subscribers.TryAdd(connectionId, subscription);
        
        try
        {
            // Send initial connection confirmation
            yield return new StreamingEvent("connection_established", JsonSerializer.Serialize(new { 
                connectionId = connectionId, 
                timestamp = DateTime.UtcNow 
            }));
            
            await foreach (var streamingEvent in subscription.ReadEventsAsync())
            {
                yield return streamingEvent;
            }
        }
        finally
        {
            _subscribers.TryRemove(connectionId, out _);
            subscription.Dispose();
            _logger.LogDebug("Connection {ConnectionId} unsubscribed from conversation", connectionId);
        }
    }

    public async Task BroadcastAsync(StreamingEvent streamingEvent)
    {
        if (_subscribers.IsEmpty) return;

        var subscribersCopy = _subscribers.Values.ToArray();
        
        foreach (var subscriber in subscribersCopy)
        {
            try
            {
                await subscriber.SendEventAsync(streamingEvent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send event to subscriber {ConnectionId}", subscriber.ConnectionId);
                // Remove failed subscriber
                _subscribers.TryRemove(subscriber.ConnectionId, out _);
            }
        }
    }

    public void Dispose()
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Dispose();
        }
        _subscribers.Clear();
    }
}

/// <summary>
/// Represents a single user's subscription to a conversation with bounded channel for backpressure.
/// </summary>
internal class UserSubscription : IDisposable
{
    public string ConnectionId { get; }
    
    private readonly Channel<StreamingEvent> _channel;
    private readonly ChannelWriter<StreamingEvent> _writer;
    private readonly CancellationToken _cancellationToken;

    public UserSubscription(string connectionId, CancellationToken cancellationToken)
    {
        ConnectionId = connectionId;
        _cancellationToken = cancellationToken;
        
        // Use bounded channel with reasonable limits to prevent memory issues
        var options = new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // Drop old events if subscriber is slow
            SingleReader = true,
            SingleWriter = false
        };
        
        _channel = Channel.CreateBounded<StreamingEvent>(options);
        _writer = _channel.Writer;
    }

    public Task SendEventAsync(StreamingEvent streamingEvent)
    {
        if (_cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        
        // Use TryWrite to avoid blocking if channel is full
        if (!_writer.TryWrite(streamingEvent))
        {
            // Channel is full - this subscriber might be slow/disconnected
            throw new InvalidOperationException($"Channel full for connection {ConnectionId}");
        }
        
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StreamingEvent> ReadEventsAsync()
    {
        try
        {
            await foreach (var streamingEvent in _channel.Reader.ReadAllAsync(_cancellationToken))
            {
                yield return streamingEvent;
            }
        }
        finally
        {
            _writer.TryComplete();
        }
    }

    public void Dispose()
    {
        _writer.TryComplete();
    }
}