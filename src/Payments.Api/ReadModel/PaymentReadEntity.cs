namespace Payments.Api.ReadModel;

public sealed class PaymentReadEntity
{
    public Guid PaymentId { get; set; }
    public string Status { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
