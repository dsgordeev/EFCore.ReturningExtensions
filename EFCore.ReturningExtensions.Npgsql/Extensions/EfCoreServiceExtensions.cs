using EFCore.ReturningExtensions.Npgsql.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.Npgsql.Extensions;

public static class EfCoreServiceExtensions
{
    public static DbContextOptionsBuilder ReplaceNpgsqlQueryServices(this DbContextOptionsBuilder optionsBuilder)
    {
        return optionsBuilder
            .ReplaceService<IQuerySqlGeneratorFactory, NpgsqlReturningQuerySqlGeneratorFactory>()
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, NpgsqlReturningQueryMethodTranslatingExpressionVisitorFactory>();
    }
}

