using EFCore.ReturningExtensions.Sqlite.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.Sqlite.Extensions;

public static class SqliteDbContextOptionsBuilderExtensions
{
    [Obsolete("The method is obsolete and will be removed. Use UseReturningExtensions instead.")]
    public static DbContextOptionsBuilder ReplaceSqliteQueryServices(this DbContextOptionsBuilder optionsBuilder)
    {
        return optionsBuilder
            .ReplaceService<IQuerySqlGeneratorFactory, SqliteReturningQuerySqlGeneratorFactory>()
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, SqliteReturningQueryMethodTranslatingExpressionVisitorFactory>();
    }

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
}

