using EFCore.ReturningExtensions.Sqlite.Query;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Sqlite.Extensions;

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