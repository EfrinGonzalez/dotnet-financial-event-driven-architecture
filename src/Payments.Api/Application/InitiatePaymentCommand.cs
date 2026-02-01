using MediatR;

namespace Payments.Api.Application;

public sealed record InitiatePaymentCommand(
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string UserId,
    string CorrelationId
) : IRequest;
