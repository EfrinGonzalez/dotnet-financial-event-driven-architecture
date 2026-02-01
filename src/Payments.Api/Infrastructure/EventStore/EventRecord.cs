using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Payments.Api.Infrastructure.EventStore;

[Table("events", Schema = "eventstore")]
public sealed class EventRecord
{
    [Key]
    public long GlobalPosition { get; set; } // identity

    [Required]
    public string StreamId { get; set; } = "";

    [Required]
    public long StreamVersion { get; set; } // 1..N

    [Required]
    public string EventType { get; set; } = "";

    [Required]
    public string PayloadJson { get; set; } = "";

    [Required]
    public DateTimeOffset OccurredAt { get; set; }

    [Required]
    public string CorrelationId { get; set; } = "";
}
