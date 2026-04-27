using EFCore.ReturningExtensions.Npgsql.Extensions;
using EFCore.ReturningExtensions.SqlServer.Extensions;
using EFCore.ReturningExtensions.Sqlite.Extensions;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ReturningExtensions.Tests.Data;

public abstract class UsersDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().ToTable("User");

        modelBuilder.Entity<User>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<User>()
            .Property(x => x.Name)
            .HasMaxLength(10);
    }
}

public class SqlUsersDbContext(DbContextOptions<SqlUsersDbContext> options) : UsersDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(connectionString: string.Empty);
        optionsBuilder.ReplaceSqlServerQueryServices();
    }
}

public class PgUsersDbContext(DbContextOptions<PgUsersDbContext> options) : UsersDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(connectionString: string.Empty);
        optionsBuilder.ReplaceNpgsqlQueryServices();
    }
}

public class Pg18UsersDbContext(DbContextOptions<Pg18UsersDbContext> options) : UsersDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(connectionString: string.Empty, o => o.SetPostgresVersion(18, 0));
        optionsBuilder.ReplaceNpgsqlQueryServices();
    }
}

public class SqliteUsersDbContext(DbContextOptions<SqliteUsersDbContext> options) : UsersDbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(connectionString: string.Empty);
        optionsBuilder.ReplaceSqliteQueryServices();
    }
}