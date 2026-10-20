using EFCore.ReturningExtensions.Npgsql.Query;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace EFCore.ReturningExtensions.Npgsql.Extensions;

public static class NpgsqlDbContextOptionsBuilderExtensions
{
    public static NpgsqlDbContextOptionsBuilder UseReturningExtensions(
        this NpgsqlDbContextOptionsBuilder npgsqlOptionsBuilder)
    {
        IRelationalDbContextOptionsBuilderInfrastructure infrastructure = npgsqlOptionsBuilder;
        IDbContextOptionsBuilderInfrastructure optionsBuilder = infrastructure.OptionsBuilder;

        var extension = infrastructure.OptionsBuilder
                            .Options
                            .FindExtension<ReturningExtensionsNpgsqlOptionsExtension>()
                        ?? new ReturningExtensionsNpgsqlOptionsExtension();

        optionsBuilder.AddOrUpdateExtension(extension);

        return npgsqlOptionsBuilder;
    }

    internal class ReturningExtensionsNpgsqlOptionsExtension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

        public void ApplyServices(IServiceCollection services)
        {
            services.AddScoped<IQuerySqlGeneratorFactory, NpgsqlReturningQuerySqlGeneratorFactory>();
            services.AddScoped<IQueryableMethodTranslatingExpressionVisitorFactory, NpgsqlReturningQueryMethodTranslatingExpressionVisitorFactory>();
        }

        public void Validate(IDbContextOptions options) { }

        private class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;
            public override string LogFragment => "using ReturningExtensionsNpgsql ";
            public override int GetServiceProviderHashCode() => 0;
            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
                => other is ExtensionInfo;
            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
        }
    }
}

