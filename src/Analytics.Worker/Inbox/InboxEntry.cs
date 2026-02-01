namespace Analytics.Worker.Inbox;

public sealed class InboxEntry
{
    public Guid MessageId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
