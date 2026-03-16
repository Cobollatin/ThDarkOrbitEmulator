using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Identity.Domain;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class IdentityReadDbContext(IOptions<IdentityDatabaseOptions> options) : DbContext
{
    private readonly string _schema = options.Value.Schema;

    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_schema);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Username).HasColumnName("username").HasMaxLength(128).IsRequired();
            entity.Property(a => a.Email).HasColumnName("email").HasMaxLength(256);
            entity.HasIndex(a => a.Username).IsUnique();
        });
    }
}
