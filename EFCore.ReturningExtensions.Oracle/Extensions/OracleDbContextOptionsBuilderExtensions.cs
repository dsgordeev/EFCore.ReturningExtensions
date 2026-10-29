using EFCore.ReturningExtensions.Oracle.Query;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Oracle.EntityFrameworkCore.Infrastructure;

namespace EFCore.ReturningExtensions.Oracle.Extensions;

public static class OracleDbContextOptionsBuilderExtensions
{
    public static OracleDbContextOptionsBuilder UseReturningExtensions(
        this OracleDbContextOptionsBuilder oracleOptionsBuilder, bool useInject = true)
    {
        IRelationalDbContextOptionsBuilderInfrastructure infrastructure = oracleOptionsBuilder;
        IDbContextOptionsBuilderInfrastructure optionsBuilder = infrastructure.OptionsBuilder;

        var extension = infrastructure.OptionsBuilder
                            .Options
                            .FindExtension<ReturningExtensionsOracleOptionsExtension>()
                        ?? new ReturningExtensionsOracleOptionsExtension();

        optionsBuilder.AddOrUpdateExtension(extension);

        return oracleOptionsBuilder;
    }

    internal class ReturningExtensionsOracleOptionsExtension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services)
        {
            services.AddScoped<IQuerySqlGeneratorFactory, OracleReturningQuerySqlGeneratorFactory>();
            services.AddScoped<IQueryableMethodTranslatingExpressionVisitorFactory, OracleReturningQueryMethodTranslatingExpressionVisitorFactory>();
        }

        public void Validate(IDbContextOptions options) { }

        private class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;
            public override string LogFragment => "using ReturningExtensionsOracle ";
            public override int GetServiceProviderHashCode() => 0;
            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
                => other is ExtensionInfo;
            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
        }
    }
}

