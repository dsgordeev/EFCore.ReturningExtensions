using EFCore.ReturningExtensions.SqlServer.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.SqlServer.Extensions;

public static class SqlServerDbContextOptionsBuilderExtensions
{
    public static SqlServerDbContextOptionsBuilder UseReturningExtensions(
        this SqlServerDbContextOptionsBuilder sqlServerOptionsBuilder)
    {
        IRelationalDbContextOptionsBuilderInfrastructure infrastructure = sqlServerOptionsBuilder;
        IDbContextOptionsBuilderInfrastructure optionsBuilder = infrastructure.OptionsBuilder;

        var extension = infrastructure.OptionsBuilder
                            .Options
                            .FindExtension<ReturningExtensionsSqlServerOptionsExtension>()
                        ?? new ReturningExtensionsSqlServerOptionsExtension();

        optionsBuilder.AddOrUpdateExtension(extension);

        return sqlServerOptionsBuilder;
    }

    internal class ReturningExtensionsSqlServerOptionsExtension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services)
        {
            services.AddScoped<IQuerySqlGeneratorFactory, SqlServerReturningQuerySqlGeneratorFactory>();
            services.AddScoped<IQueryableMethodTranslatingExpressionVisitorFactory, SqlServerReturningQueryMethodTranslatingExpressionVisitorFactory>();
        }

        public void Validate(IDbContextOptions options) { }

        private class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;
            public override string LogFragment => "using ReturningExtensionsSqlServer ";
            public override int GetServiceProviderHashCode() => 0;
            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
                => other is ExtensionInfo;
            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
        }
    }
}

