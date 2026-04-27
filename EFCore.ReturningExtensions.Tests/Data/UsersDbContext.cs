using EFCore.ReturningExtensions.Npgsql.Extensions;
using EFCore.ReturningExtensions.SqlServer.Extensions;
using EFCore.ReturningExtensions.Sqlite.Extensions;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ReturningExtensions.Tests.Data;

public abstract class UsersDbContext(DbContextOptions options) : DbContext(options)
{
    protected bool UseInject = true;

    public DbSet<User> Users { get; set; }

    public DbSet<Account> Accounts { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().ToTable("User");

        modelBuilder.Entity<User>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<User>()
            .Property(x => x.Name)
            .HasMaxLength(10);

        modelBuilder.Entity<Account>().ToTable("Account");

        modelBuilder.Entity<Account>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<Account>()
            .Property(x => x.Name)
            .HasMaxLength(10);

        modelBuilder.Entity<Account>()
            .HasOne(x => x.Type)
            .WithMany()
            .HasForeignKey(x => x.TypeId)
            .HasPrincipalKey(x => x.Id);

        modelBuilder.Entity<AccountType>().ToTable("AccountType");

        modelBuilder.Entity<AccountType>()
            .HasOne(x => x.SubType)
            .WithMany()
            .HasForeignKey(x => x.SubTypeId)
            .HasPrincipalKey(x => x.Id);

        modelBuilder.Entity<AccountType>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<AccountType>()
            .Property(x => x.TypeName)
            .HasMaxLength(10);

        modelBuilder.Entity<AccountSubType>().ToTable("AccountSubType");

        modelBuilder.Entity<AccountSubType>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<AccountSubType>()
            .Property(x => x.StName)
            .HasMaxLength(10);
    }
}

public class SqlUsersDbContext(DbContextOptions<SqlUsersDbContext> options) : UsersDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(connectionString: string.Empty, o =>
        {
            o.UseReturningExtensions(UseInject);
        });
    }
}

public class PgUsersDbContext(DbContextOptions<PgUsersDbContext> options) : UsersDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(connectionString: string.Empty, o =>
        {
            o.UseReturningExtensions(UseInject);
        });
    }
}

public class Pg18UsersDbContext(DbContextOptions<Pg18UsersDbContext> options) : UsersDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(connectionString: string.Empty, o =>
        {
            o.SetPostgresVersion(18, 0);
            o.UseReturningExtensions(UseInject);
        });
    }
}

public class SqliteUsersDbContext(DbContextOptions<SqliteUsersDbContext> options) : UsersDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(connectionString: string.Empty, o =>
        {
            o.UseReturningExtensions(UseInject);
        });
    }
}
