using MayFly.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace MayFly.Api.Data;

public class MayFlyContext(DbContextOptions<MayFlyContext> options) : DbContext(options)
{
    public DbSet<Instance> Instances => Set<Instance>();
    public DbSet<QueryLog> QueryLogs => Set<QueryLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Instance>().HasIndex(i => i.CapabilityToken).IsUnique();
        b.Entity<Instance>().HasIndex(i => i.SessionId);
        b.Entity<Instance>().HasIndex(i => new { i.CreatorIp, i.State });
        b.Entity<Instance>().Property(i => i.State).HasConversion<string>();
        b.Entity<QueryLog>().HasIndex(q => q.InstanceId);
    }
}
