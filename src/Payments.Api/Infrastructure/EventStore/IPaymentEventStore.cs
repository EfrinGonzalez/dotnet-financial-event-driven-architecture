using Payments.Api.Domain;

namespace Payments.Api.Infrastructure.EventStore;

public interface IPaymentEventStore
{
    Task<(long Version, List<IDomainEvent> Events)> LoadAsync(Guid paymentId, CancellationToken ct);
    Task AppendAsync(Guid paymentId, long expectedVersion, IReadOnlyList<IDomainEvent> newEvents, string correlationId, CancellationToken ct);
}
