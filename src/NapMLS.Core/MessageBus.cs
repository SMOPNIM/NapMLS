using System.Collections.Concurrent;

namespace NapMLS.Core;

/// <summary>
/// Event types published through the bus.
/// </summary>
public abstract class MlsEvent
{
    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>An application message was received and decrypted.</summary>
public sealed class MessageReceivedEvent : MlsEvent
{
    public string GroupHash { get; init; } = "";
    public long Sender { get; init; }
    public long Epoch { get; init; }
    public byte[] Plaintext { get; init; } = [];
}

/// <summary>A Commit was received.</summary>
public sealed class CommitReceivedEvent : MlsEvent
{
    public string GroupHash { get; init; } = "";
    public long Sender { get; init; }
    public long Epoch { get; init; }
    public byte[] Ciphertext { get; init; } = [];
}

/// <summary>A Welcome was received.</summary>
public sealed class WelcomeReceivedEvent : MlsEvent
{
    public string GroupHash { get; init; } = "";
    public long Epoch { get; init; }
    public byte[] Ciphertext { get; init; } = [];
}

/// <summary>A resync was requested.</summary>
public sealed class ResyncRequestedEvent : MlsEvent
{
    public string GroupHash { get; init; } = "";
    public long RequesterEpoch { get; init; }
    public long Sender { get; init; }
}

/// <summary>Connection state changed.</summary>
public sealed class ConnectionChangedEvent : MlsEvent
{
    public bool IsConnected { get; init; }
}

/// <summary>
/// Simple pub/sub message bus for MLS events.
/// Subscribers receive events on the publishing thread (synchronous).
/// </summary>
public sealed class MessageBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();

    /// <summary>Subscribe to an event type.</summary>
    public IDisposable Subscribe<T>(Action<T> handler) where T : MlsEvent
    {
        var type = typeof(T);
        var list = _handlers.GetOrAdd(type, _ => []);
        lock (list) { list.Add(handler); }

        return new Unsubscriber(() =>
        {
            lock (list) { list.Remove(handler); }
        });
    }

    /// <summary>Publish an event to all subscribers.</summary>
    public void Publish<T>(T evt) where T : MlsEvent
    {
        if (!_handlers.TryGetValue(typeof(T), out var list))
            return;

        Delegate[] snapshot;
        lock (list) { snapshot = list.ToArray(); }

        foreach (var handler in snapshot)
        {
            try
            {
                ((Action<T>)handler)(evt);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[MessageBus] Handler error: {ex.Message}");
            }
        }
    }

    private sealed class Unsubscriber(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
