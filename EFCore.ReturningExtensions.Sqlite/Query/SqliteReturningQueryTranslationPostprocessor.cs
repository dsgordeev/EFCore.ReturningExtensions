using System.Diagnostics;
using System.Linq.Expressions;
using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;

namespace EFCore.ReturningExtensions.Sqlite.Query;

internal class SqliteReturningQueryTranslationPostprocessorFactory(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies)
    : IQueryTranslationPostprocessorFactory
{
    protected virtual QueryTranslationPostprocessorDependencies Dependencies { get; } = dependencies;

    protected virtual RelationalQueryTranslationPostprocessorDependencies RelationalDependencies { get; } = relationalDependencies;

    [DebuggerStepThrough]
    public virtual QueryTranslationPostprocessor Create(QueryCompilationContext queryCompilationContext)
        => new SqliteReturningQueryTranslationPostprocessor(
            Dependencies, RelationalDependencies, (SqliteQueryCompilationContext)queryCompilationContext);
}

internal class SqliteReturningQueryTranslationPostprocessor(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    SqliteQueryCompilationContext queryCompilationContext)
    : SqliteQueryTranslationPostprocessor(dependencies, relationalDependencies, queryCompilationContext)
{
    public override Expression Process(Expression query)
    {
        if (query is ShapedQueryExpression shaped)
        {
            if (shaped.QueryExpression is ReturningUpdateExpression returningUpdate)
            {
                var select = (SelectExpression)base.Process(returningUpdate.UpdateExpression.SelectExpression);

                var update = returningUpdate.UpdateExpression.Update(select, returningUpdate.UpdateExpression.ColumnValueSetters);

                return shaped.UpdateQueryExpression(returningUpdate.Update(update, returningUpdate.Projections));
            }

            if (shaped.QueryExpression is ReturningDeleteExpression returningDelete)
            {
                var select = (SelectExpression)base.Process(returningDelete.DeleteExpression.SelectExpression);

                var delete = returningDelete.DeleteExpression.Update(returningDelete.DeleteExpression.Table, select);

                return shaped.UpdateQueryExpression(returningDelete.Update(delete, returningDelete.Projections));
            }
        }

        return base.Process(query);
    }
}