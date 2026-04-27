using EFCore.ReturningExtensions.Sqlite.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.Sqlite.Extensions;

public static class EfCoreServiceExtensions
{
    public static DbContextOptionsBuilder ReplaceSqliteQueryServices(this DbContextOptionsBuilder optionsBuilder)
    {
        return optionsBuilder
            .ReplaceService<IQuerySqlGeneratorFactory, SqliteReturningQuerySqlGeneratorFactory>()
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, SqliteReturningQueryMethodTranslatingExpressionVisitorFactory>();
    }
}

