using Microsoft.EntityFrameworkCore;

namespace Analytics.Worker.Inbox;

public sealed class InboxDbContext : DbContext
{
    public InboxDbContext(DbContextOptions<InboxDbContext> options) : base(options) { }

    public DbSet<InboxEntry> Inbox => Set<InboxEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxEntry>()
            .ToTable("inbox", schema: "analytics")
            .HasKey(x => x.MessageId);
    }
}
