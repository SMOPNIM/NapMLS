namespace NapMLS.Core;

/// <summary>
/// Abstraction for sending MLS messages over a transport.
/// Implementations: InMemoryTransport (testing), NapCatTransport (production).
/// </summary>
public interface IMessageTransport
{
    /// <summary>Send a group message (to QQ group via NapCat).</summary>
    Task SendGroupMessageAsync(long groupId, string text, CancellationToken ct = default);

    /// <summary>Send a private message (to QQ user via NapCat).</summary>
    Task SendPrivateMessageAsync(long userId, string text, CancellationToken ct = default);

    /// <summary>Whether the transport is connected and ready.</summary>
    bool IsConnected { get; }
}

/// <summary>
/// In-memory message transport for testing.
/// Routes messages between MlsClient instances via callbacks.
/// </summary>
public sealed class InMemoryTransport : IMessageTransport
{
    private readonly Dictionary<long, Action<string>> _groupHandlers = new();
    private readonly Dictionary<long, Action<string>> _privateHandlers = new();

    public bool IsConnected => true;

    /// <summary>Register a handler for group messages (simulates QQ group).</summary>
    public void RegisterGroupHandler(long groupId, Action<string> handler)
    {
        _groupHandlers[groupId] = handler;
    }

    /// <summary>Register a handler for private messages (simulates QQ user).</summary>
    public void RegisterPrivateHandler(long userId, Action<string> handler)
    {
        _privateHandlers[userId] = handler;
    }

    public Task SendGroupMessageAsync(long groupId, string text, CancellationToken ct = default)
    {
        if (_groupHandlers.TryGetValue(groupId, out var handler))
        {
            handler(text);
        }
        return Task.CompletedTask;
    }

    public Task SendPrivateMessageAsync(long userId, string text, CancellationToken ct = default)
    {
        if (_privateHandlers.TryGetValue(userId, out var handler))
        {
            handler(text);
        }
        return Task.CompletedTask;
    }
}
