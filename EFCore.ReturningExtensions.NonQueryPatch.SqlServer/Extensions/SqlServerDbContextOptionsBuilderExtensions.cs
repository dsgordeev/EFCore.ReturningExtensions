using EFCore.ReturningExtensions.NonQueryPatch.SqlServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.NonQueryPatch.SqlServer.Extensions;

public static class SqlServerDbContextOptionsBuilderExtensions
{
    public static SqlServerDbContextOptionsBuilder UseNonQueryPatchExtensions(
        this SqlServerDbContextOptionsBuilder sqlServerOptionsBuilder)
    {
        IRelationalDbContextOptionsBuilderInfrastructure infrastructure = sqlServerOptionsBuilder;
        IDbContextOptionsBuilderInfrastructure optionsBuilder = infrastructure.OptionsBuilder;

        var extension = infrastructure.OptionsBuilder
                            .Options
                            .FindExtension<NonQueryPatchExtensionsSqlServerOptionsExtension>()
                        ?? new NonQueryPatchExtensionsSqlServerOptionsExtension();

        optionsBuilder.AddOrUpdateExtension(extension);

        return sqlServerOptionsBuilder;
    }

    internal class NonQueryPatchExtensionsSqlServerOptionsExtension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services)
        {
            services.AddScoped<IQueryableMethodTranslatingExpressionVisitorFactory, SqlServerQueryableMethodTranslatingExpressionVisitorFactoryPatch>();
            services.AddScoped<IShapedQueryCompilingExpressionVisitorFactory, RelationalShapedQueryCompilingExpressionVisitorFactoryPatch>();
            services.AddScoped<IQuerySqlGeneratorFactory, SqlServerQuerySqlGeneratorFactoryPatch>();
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
