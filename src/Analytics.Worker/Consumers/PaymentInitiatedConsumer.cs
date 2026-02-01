using Analytics.Worker.Inbox;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts;

namespace Analytics.Worker.Consumers;

public sealed class PaymentInitiatedConsumer : IConsumer<PaymentInitiatedIntegration>
{
    private readonly InboxDbContext _db;
    private readonly ILogger<PaymentInitiatedConsumer> _log;

    public PaymentInitiatedConsumer(InboxDbContext db, ILogger<PaymentInitiatedConsumer> log)
    {
        _db = db;
        _log = log;
    }

    public async Task Consume(ConsumeContext<PaymentInitiatedIntegration> context)
    {
        var msg = context.Message;

        // Inbox idempotency: ignore duplicates
        if (await _db.Inbox.AnyAsync(x => x.MessageId == msg.MessageId, context.CancellationToken))
        {
            _log.LogInformation("Duplicate ignored MessageId={MessageId} Corr={Corr}",
                msg.MessageId, msg.CorrelationId);
            return;
        }

        _db.Inbox.Add(new InboxEntry { MessageId = msg.MessageId, ProcessedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync(context.CancellationToken);

        _log.LogInformation("ANALYTICS processed PaymentId={PaymentId} Amount={Amount} Corr={Corr}",
            msg.PaymentId, msg.Amount, msg.CorrelationId);

        // Uncomment to test retries + DLQ:
        // throw new InvalidOperationException("Simulated failure");
    }
}
