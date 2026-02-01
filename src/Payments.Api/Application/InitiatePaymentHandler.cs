using MassTransit;
using MediatR;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;
using Payments.Api.Infrastructure.EventStore;
using Payments.Api.ReadModel;
using Shared.Contracts;

namespace Payments.Api.Application;

public sealed class InitiatePaymentHandler : IRequestHandler<InitiatePaymentCommand>
{
    private readonly PaymentsDbContext _db;
    private readonly IPaymentEventStore _store;
    private readonly PaymentProjector _projector;
    private readonly IPublishEndpoint _publish;

    public InitiatePaymentHandler(
        PaymentsDbContext db,
        IPaymentEventStore store,
        PaymentProjector projector,
        IPublishEndpoint publish)
    {
        _db = db;
        _store = store;
        _projector = projector;
        _publish = publish;
    }

    public async Task Handle(InitiatePaymentCommand request, CancellationToken ct)
    {
        // One transaction: append domain events + update read model + persist outbox message
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var (version, history) = await _store.LoadAsync(request.PaymentId, ct);
        var agg = PaymentAggregate.Rehydrate(history);

        agg.Initiate(request.PaymentId, request.Amount, request.Currency, request.UserId);

        var newEvents = agg.Uncommitted.ToList();

        await _store.AppendAsync(request.PaymentId, version, newEvents, request.CorrelationId, ct);

        foreach (var ev in newEvents)
            await _projector.ProjectAsync(ev, ct);

        // Publish integration events (with UseBusOutbox enabled, Publish is stored to outbox inside this transaction)
        foreach (var ev in newEvents)
        {
            if (ev is PaymentInitiated e)
            {
                await _publish.Publish(new PaymentInitiatedIntegration(
                    MessageId: Guid.NewGuid(),
                    OccurredAt: DateTimeOffset.UtcNow,
                    CorrelationId: request.CorrelationId,
                    Version: 1,
                    PaymentId: e.PaymentId,
                    Amount: e.Amount,
                    Currency: e.Currency,
                    UserId: e.UserId
                ), ct);
            }
        }

        await tx.CommitAsync(ct);
        agg.ClearUncommitted();
    }
}
