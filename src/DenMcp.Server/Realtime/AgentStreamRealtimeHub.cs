using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DenMcp.Core.Models;

namespace DenMcp.Server.Realtime;

public sealed record AgentStreamRealtimeFilter(
    string? ProjectId = null,
    int? TaskId = null,
    string? EventTypePrefix = null);

public sealed class AgentStreamRealtimeHub
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public async IAsyncEnumerable<AgentStreamEntry> SubscribeAsync(
        AgentStreamRealtimeFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AgentStreamEntry>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _subscribers[id] = new Subscriber(filter, channel);
        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken))
                yield return entry;
        }
        finally
        {
            if (_subscribers.TryRemove(id, out var subscriber))
                subscriber.Channel.Writer.TryComplete();
        }
    }

    public void Publish(AgentStreamEntry entry)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            if (Matches(subscriber.Filter, entry))
                subscriber.Channel.Writer.TryWrite(entry);
        }
    }

    private static bool Matches(AgentStreamRealtimeFilter filter, AgentStreamEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(filter.ProjectId) &&
            !string.Equals(entry.ProjectId, filter.ProjectId, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.TaskId is not null && entry.TaskId != filter.TaskId)
            return false;

        if (!string.IsNullOrWhiteSpace(filter.EventTypePrefix) &&
            !entry.EventType.StartsWith(filter.EventTypePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private sealed record Subscriber(
        AgentStreamRealtimeFilter Filter,
        Channel<AgentStreamEntry> Channel);
}
