using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;

namespace Payments.Api.Infrastructure.EventStore;

public sealed class PaymentEventStore : IPaymentEventStore
{
    private readonly PaymentsDbContext _db;

    public PaymentEventStore(PaymentsDbContext db) => _db = db;

    private static string StreamId(Guid id) => $"payment-{id}";

    public async Task<(long Version, List<IDomainEvent> Events)> LoadAsync(Guid paymentId, CancellationToken ct)
    {
        var streamId = StreamId(paymentId);

        var rows = await _db.Events
            .Where(x => x.StreamId == streamId)
            .OrderBy(x => x.StreamVersion)
            .ToListAsync(ct);

        var events = rows.Select(Deserialize).ToList();
        var version = rows.Count == 0 ? 0 : rows.Max(x => x.StreamVersion);
        return (version, events);
    }

    public async Task AppendAsync(Guid paymentId, long expectedVersion, IReadOnlyList<IDomainEvent> newEvents, string correlationId, CancellationToken ct)
    {
        var streamId = StreamId(paymentId);

        long nextVersion = expectedVersion;

        foreach (var ev in newEvents)
        {
            nextVersion++;
            _db.Events.Add(new EventRecord
            {
                StreamId = streamId,
                StreamVersion = nextVersion,
                EventType = ev.GetType().FullName!,
                PayloadJson = JsonSerializer.Serialize(ev, ev.GetType()),
                OccurredAt = ev.OccurredAt,
                CorrelationId = correlationId
            });
        }

        // Optimistic concurrency relies on unique (StreamId, StreamVersion)
        await _db.SaveChangesAsync(ct);
    }

    private static IDomainEvent Deserialize(EventRecord r)
    {
        // Minimal type registry
        return r.EventType.EndsWith(nameof(Payments.Api.Domain.PaymentInitiated))
            ? JsonSerializer.Deserialize<Payments.Api.Domain.PaymentInitiated>(r.PayloadJson)!
            : throw new InvalidOperationException($"Unknown domain event type: {r.EventType}");
    }
}
