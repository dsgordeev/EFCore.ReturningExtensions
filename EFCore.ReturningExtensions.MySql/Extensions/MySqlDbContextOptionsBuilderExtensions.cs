using EFCore.ReturningExtensions.MySql.Query;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.MySql.Extensions;

public static class MySqlDbContextOptionsBuilderExtensions
{
    public static MySqlDbContextOptionsBuilder UseReturningExtensions(
        this MySqlDbContextOptionsBuilder mySqlOptionsBuilder, bool useInject = true)
    {
        IRelationalDbContextOptionsBuilderInfrastructure infrastructure = mySqlOptionsBuilder;
        IDbContextOptionsBuilderInfrastructure optionsBuilder = infrastructure.OptionsBuilder;

        var extension = infrastructure.OptionsBuilder
                            .Options
                            .FindExtension<ReturningExtensionsMySqlOptionsExtension>()
                        ?? new ReturningExtensionsMySqlOptionsExtension(useInject);

        optionsBuilder.AddOrUpdateExtension(extension);

        return mySqlOptionsBuilder;
    }

    internal class ReturningExtensionsMySqlOptionsExtension(bool useInject) : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);
        public bool UseInject { get; } = useInject;

        public void ApplyServices(IServiceCollection services)
        {
            services.AddScoped<IQuerySqlGeneratorFactory, MySqlReturningQuerySqlGeneratorFactory>();
            services.AddScoped<IQueryableMethodTranslatingExpressionVisitorFactory, MySqlReturningQueryableMethodTranslatingExpressionVisitorFactory>();
            if (!UseInject)
            {
                services.AddScoped<IQueryTranslationPostprocessorFactory, MySqlReturningQueryTranslationPostprocessorFactory>();
                services.AddScoped<IShapedQueryCompilingExpressionVisitorFactory, ReturningRelationalShapedQueryCompilingExpressionVisitorFactory>();
                services.AddScoped<IRelationalParameterBasedSqlProcessorFactory, MySqlReturningRelationalParameterBasedSqlProcessorFactory>();
            }
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

