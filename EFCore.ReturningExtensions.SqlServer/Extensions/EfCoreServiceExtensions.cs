using EFCore.ReturningExtensions.NonQueryPatch.Infrastructure;
using EFCore.ReturningExtensions.SqlServer.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.SqlServer.Extensions;

public static class EfCoreServiceExtensions
{
    public static DbContextOptionsBuilder ReplaceSqlServerQueryServices(this DbContextOptionsBuilder optionsBuilder)
    {
        // patch
        optionsBuilder
            .ReplaceService<IQueryCompilationContextFactory, SqlServerQueryCompilationContextFactoryPatch>()
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, SqlServerQueryableMethodTranslatingExpressionVisitorFactoryPatch>()
            .ReplaceService<IShapedQueryCompilingExpressionVisitorFactory, RelationalShapedQueryCompilingExpressionVisitorFactoryPatch>()
            .ReplaceService<IQuerySqlGeneratorFactory, SqlServerQuerySqlGeneratorFactoryPatch>();


        return optionsBuilder
            .ReplaceService<IQuerySqlGeneratorFactory, SqlServerReturningQuerySqlGeneratorFactory>()
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, SqlServerReturningQueryMethodTranslatingExpressionVisitorFactory>();
    }
}

