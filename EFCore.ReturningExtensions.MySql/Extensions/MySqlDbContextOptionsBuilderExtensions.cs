using EFCore.ReturningExtensions.MySql.Query;
using EFCore.ReturningExtensions.NonQueryPatch.MySql.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.MySql.Extensions;

public static class MySqlDbContextOptionsBuilderExtensions
{
    public static MySqlDbContextOptionsBuilder UseReturningExtensions(
        this MySqlDbContextOptionsBuilder mySqlOptionsBuilder)
    {
        IRelationalDbContextOptionsBuilderInfrastructure infrastructure = mySqlOptionsBuilder;
        IDbContextOptionsBuilderInfrastructure optionsBuilder = infrastructure.OptionsBuilder;

        var extension = infrastructure.OptionsBuilder
                            .Options
                            .FindExtension<ReturningExtensionsMySqlOptionsExtension>()
                        ?? new ReturningExtensionsMySqlOptionsExtension();

        optionsBuilder.AddOrUpdateExtension(extension);

        return mySqlOptionsBuilder;
    }

    internal class ReturningExtensionsMySqlOptionsExtension() : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);
        
        public void ApplyServices(IServiceCollection services)
        {
            services.AddScoped<IQuerySqlGeneratorFactory, MySqlReturningQuerySqlGeneratorFactory>();
            services.AddScoped<IShapedQueryCompilingExpressionVisitorFactory, RelationalShapedQueryCompilingExpressionVisitorFactoryPatch>();
            services.AddScoped<IQueryableMethodTranslatingExpressionVisitorFactory, MySqlReturningQueryableMethodTranslatingExpressionVisitorFactory>();
        }

        public void Validate(IDbContextOptions options) { }

        private class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;
            public override string LogFragment => "using ReturningExtensionsMySql ";
            public override int GetServiceProviderHashCode() => 0;
            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
                => other is ExtensionInfo;
            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
        }
    }
}

