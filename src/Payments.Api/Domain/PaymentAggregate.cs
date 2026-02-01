namespace Payments.Api.Domain;

public sealed class PaymentAggregate
{
    public Guid PaymentId { get; private set; }
    public string Status { get; private set; } = "None";
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public string UserId { get; private set; } = "";

    private readonly List<IDomainEvent> _uncommitted = new();
    public IReadOnlyList<IDomainEvent> Uncommitted => _uncommitted;

    public static PaymentAggregate Rehydrate(IEnumerable<IDomainEvent> history)
    {
        var agg = new PaymentAggregate();
        foreach (var ev in history) agg.Apply(ev);
        return agg;
    }

    public void Initiate(Guid paymentId, decimal amount, string currency, string userId)
    {
        if (Status != "None") throw new InvalidOperationException("Payment already exists.");
        if (amount <= 0) throw new InvalidOperationException("Amount must be > 0.");

        Raise(new PaymentInitiated(Guid.NewGuid(), DateTimeOffset.UtcNow, 1, paymentId, amount, currency, userId));
    }

    private void Raise(IDomainEvent ev)
    {
        Apply(ev);
        _uncommitted.Add(ev);
    }

    private void Apply(IDomainEvent ev)
    {
        if (ev is PaymentInitiated e)
        {
            PaymentId = e.PaymentId;
            Amount = e.Amount;
            Currency = e.Currency;
            UserId = e.UserId;
            Status = "Initiated";
        }
    }

    public void ClearUncommitted() => _uncommitted.Clear();
}
