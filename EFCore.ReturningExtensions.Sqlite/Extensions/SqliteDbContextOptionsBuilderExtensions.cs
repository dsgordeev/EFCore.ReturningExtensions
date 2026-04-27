using EFCore.ReturningExtensions.Sqlite.Query;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Sqlite.Extensions;

public static class SqliteDbContextOptionsBuilderExtensions
{
    public static SqliteDbContextOptionsBuilder UseReturningExtensions(
        this SqliteDbContextOptionsBuilder sqliteOptionsBuilder)
    {
        IRelationalDbContextOptionsBuilderInfrastructure infrastructure = sqliteOptionsBuilder;
        IDbContextOptionsBuilderInfrastructure optionsBuilder = infrastructure.OptionsBuilder;

        var extension = infrastructure.OptionsBuilder
                            .Options
                            .FindExtension<ReturningExtensionsSqliteOptionsExtension>()
                        ?? new ReturningExtensionsSqliteOptionsExtension();

        optionsBuilder.AddOrUpdateExtension(extension);

        return sqliteOptionsBuilder;
    }

    internal class ReturningExtensionsSqliteOptionsExtension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services)
        {
            services.AddScoped<IQuerySqlGeneratorFactory, SqliteReturningQuerySqlGeneratorFactory>();
            services.AddScoped<IQueryableMethodTranslatingExpressionVisitorFactory, SqliteReturningQueryMethodTranslatingExpressionVisitorFactory>();
        }

        public void Validate(IDbContextOptions options) { }

        private class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;
            public override string LogFragment => "using ReturningExtensionsSqlite ";
            public override int GetServiceProviderHashCode() => 0;
            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
                => other is ExtensionInfo;
            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
        }
    }


}

