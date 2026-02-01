namespace Payments.Api.Domain;

public sealed record PaymentInitiated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int Version,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string UserId
) : IDomainEvent;
