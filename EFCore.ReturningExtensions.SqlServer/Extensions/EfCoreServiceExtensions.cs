using EFCore.ReturningExtensions.SqlServer.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.SqlServer.Extensions;

public static class EfCoreServiceExtensions
{
    public static DbContextOptionsBuilder ReplaceSqlServerQueryServices(this DbContextOptionsBuilder optionsBuilder)
    {
        return optionsBuilder
            .ReplaceService<IQuerySqlGeneratorFactory, SqlServerReturningQuerySqlGeneratorFactory>()
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, SqlServerReturningQueryMethodTranslatingExpressionVisitorFactory>();
    }
}

