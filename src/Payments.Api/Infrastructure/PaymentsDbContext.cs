using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Infrastructure.EventStore;
using Payments.Api.ReadModel;

namespace Payments.Api.Infrastructure;

public sealed class PaymentsDbContext : DbContext
{
    public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : base(options) { }

    // Event store
    public DbSet<EventRecord> Events => Set<EventRecord>();

    // Read model projection
    public DbSet<PaymentReadEntity> PaymentsRead => Set<PaymentReadEntity>();

    // MassTransit EF Outbox entities
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
    public DbSet<OutboxStateEntity> OutboxStates => Set<OutboxStateEntity>();
    public DbSet<InboxStateEntity> InboxStates => Set<InboxStateEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecord>()
            .HasIndex(x => new { x.StreamId, x.StreamVersion })
            .IsUnique();

        modelBuilder.Entity<EventRecord>()
            .HasIndex(x => x.StreamId);

        modelBuilder.Entity<PaymentReadEntity>()
            .ToTable("payments_read", schema: "readmodel")
            .HasKey(x => x.PaymentId);

        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddInboxStateEntity();
    }
}
