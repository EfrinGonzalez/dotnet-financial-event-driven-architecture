using Microsoft.EntityFrameworkCore;
using Payments.Api.Domain;
using Payments.Api.Infrastructure;

namespace Payments.Api.ReadModel;

public sealed class PaymentProjector
{
    private readonly PaymentsDbContext _db;

    public PaymentProjector(PaymentsDbContext db) => _db = db;

    public async Task ProjectAsync(IDomainEvent ev, CancellationToken ct)
    {
        switch (ev)
        {
            case PaymentInitiated e:
            {
                // Minimal idempotency: ignore if already projected
                var exists = await _db.PaymentsRead.AnyAsync(x => x.PaymentId == e.PaymentId, ct);
                if (exists) return;

                _db.PaymentsRead.Add(new PaymentReadEntity
                {
                    PaymentId = e.PaymentId,
                    Status = "Initiated",
                    Amount = e.Amount,
                    Currency = e.Currency,
                    UserId = e.UserId,
                    UpdatedAt = DateTimeOffset.UtcNow
                });

                await _db.SaveChangesAsync(ct);
                break;
            }

            default:
                throw new InvalidOperationException($"No projector defined for {ev.GetType().Name}");
        }
    }
}
