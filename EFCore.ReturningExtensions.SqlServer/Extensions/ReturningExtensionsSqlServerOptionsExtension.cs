using EFCore.ReturningExtensions.SqlServer.Query;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.SqlServer.Extensions;

internal class ReturningExtensionsSqlServerOptionsExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.AddScoped<IQuerySqlGeneratorFactory, SqlServerReturningQuerySqlGeneratorFactory>();
        services.AddScoped<IQueryableMethodTranslatingExpressionVisitorFactory, SqlServerReturningQueryMethodTranslatingExpressionVisitorFactory>();
    }

    public void Validate(IDbContextOptions options) { }

    private class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(IDbContextOptionsExtension extension) : base(extension)
        {
        }

        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "using ReturningExtensionsSqlServer ";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
    }
}