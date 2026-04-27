using EFCore.ReturningExtensions.SqlServer.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.SqlServer.Extensions;

public static class SqlServerDbContextOptionsBuilderExtensions
{
    [Obsolete("The method is obsolete and will be removed. Use UseReturningExtensions instead.")]
    public static DbContextOptionsBuilder ReplaceSqlServerQueryServices(this DbContextOptionsBuilder optionsBuilder)
    {
        return optionsBuilder
            .ReplaceService<IQuerySqlGeneratorFactory, SqlServerReturningQuerySqlGeneratorFactory>()
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, SqlServerReturningQueryMethodTranslatingExpressionVisitorFactory>();
    }

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
}

