namespace Shared.Contracts;

public interface IIntegrationEvent
{
    Guid MessageId { get; }
    DateTimeOffset OccurredAt { get; }
    string CorrelationId { get; }
    int Version { get; }
}

public sealed record PaymentInitiatedIntegration(
    Guid MessageId,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    int Version,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string UserId
) : IIntegrationEvent;
