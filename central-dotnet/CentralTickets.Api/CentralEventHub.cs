using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CentralTickets.Api;

/// <summary>Shares ticket and usage polling across all connected browser clients.</summary>
public sealed class CentralEventHub(TicketCatalog tickets, UsageService usage) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Channel<CentralStreamEvent>> _subscribers = new();
    private string? _ticketDigest;
    private DateTimeOffset _lastClaude = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCodex = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;

    public CentralEventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<CentralStreamEvent>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        return new CentralEventSubscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_subscribers.IsEmpty)
            {
                try { await timer.WaitForNextTickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                continue;
            }
            try { await PublishChanges(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { /* A failed provider read must not stop the other live feeds. */ }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task PublishChanges(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastClaude >= TimeSpan.FromSeconds(5))
        {
            Publish("usage", usage.ReadClaude());
            _lastClaude = now;
        }
        if (now - _lastCodex >= TimeSpan.FromMinutes(1))
        {
            try { Publish("usage", await usage.ReadCodex(cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* Codex can be unavailable while Claude and tickets remain live. */ }
            _lastCodex = now;
        }

        var current = tickets.List("all", null, null, null, 500);
        var digest = JsonSerializer.Serialize(current.Select(ticket => new { ticket.Id, ticket.Stage, ticket.UpdatedAt, ticket.Session, ticket.Prepared }));
        if (!string.Equals(digest, _ticketDigest, StringComparison.Ordinal))
        {
            Publish("tickets", new JsonObject { ["at"] = now.ToString("O") });
            _ticketDigest = digest;
        }
        if (now - _lastHeartbeat >= TimeSpan.FromSeconds(15))
        {
            Publish("heartbeat", new JsonObject { ["at"] = now.ToString("O") });
            _lastHeartbeat = now;
        }
    }

    private void Publish(string name, JsonNode data)
    {
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(new CentralStreamEvent(name, data.DeepClone()));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
        _subscribers.Clear();
        await base.StopAsync(cancellationToken);
    }
}

public sealed record CentralStreamEvent(string Name, JsonNode Data);

public sealed class CentralEventSubscription(ChannelReader<CentralStreamEvent> reader, Action dispose) : IAsyncDisposable
{
    public ChannelReader<CentralStreamEvent> Reader { get; } = reader;
    public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
}
