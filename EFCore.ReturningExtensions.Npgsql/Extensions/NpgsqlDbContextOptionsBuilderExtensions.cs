using EFCore.ReturningExtensions.Npgsql.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace EFCore.ReturningExtensions.Npgsql.Extensions;

public static class NpgsqlDbContextOptionsBuilderExtensions
{
    [Obsolete("The method is obsolete and will be removed. Use UseReturningExtensions instead.")]
    public static DbContextOptionsBuilder ReplaceNpgsqlQueryServices(this DbContextOptionsBuilder optionsBuilder)
    {
        return optionsBuilder
            .ReplaceService<IQuerySqlGeneratorFactory, NpgsqlReturningQuerySqlGeneratorFactory>()
            .ReplaceService<IQueryableMethodTranslatingExpressionVisitorFactory, NpgsqlReturningQueryMethodTranslatingExpressionVisitorFactory>();
    }

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
}

