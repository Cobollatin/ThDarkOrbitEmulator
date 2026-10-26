using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Identity.Domain;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class IdentityReadDbContext : DbContext
{
    private readonly string _schema;

    public IdentityReadDbContext(
        DbContextOptions<IdentityReadDbContext> dbContextOptions,
        IOptions<IdentityDatabaseOptions> options)
        : base(dbContextOptions)
    {
        _schema = options.Value.Schema;
    }

    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_schema);

        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.Username).HasColumnName("username").HasMaxLength(128).IsRequired();
            entity.Property(a => a.Email).HasColumnName("email").HasMaxLength(256);
            entity.Property(a => a.GameUserId).HasColumnName("game_user_id").IsRequired();
            entity.Property(a => a.Company).HasColumnName("company").HasMaxLength(3);
            entity.HasIndex(a => a.Username).IsUnique();
            entity.HasIndex(a => a.GameUserId).IsUnique();
        });
    }
}
