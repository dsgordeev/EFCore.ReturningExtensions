using System.Diagnostics;
using System.Linq.Expressions;
using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microting.EntityFrameworkCore.MySql.Infrastructure.Internal;
using Microting.EntityFrameworkCore.MySql.Query.ExpressionVisitors.Internal;
using Microting.EntityFrameworkCore.MySql.Query.Internal;

namespace EFCore.ReturningExtensions.MySql.Query;

internal class MySqlReturningQueryTranslationPostprocessorFactory(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    IMySqlOptions options,
    ISqlExpressionFactory factory)
    : IQueryTranslationPostprocessorFactory
{
    [DebuggerStepThrough]
    public QueryTranslationPostprocessor Create(QueryCompilationContext queryCompilationContext)
        => new MySqlReturningQueryTranslationPostprocessor(
            dependencies, relationalDependencies, (MySqlQueryCompilationContext)queryCompilationContext, options, (MySqlSqlExpressionFactory)factory);
}

public class MySqlReturningQueryTranslationPostprocessor(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    MySqlQueryCompilationContext queryCompilationContext,
    IMySqlOptions options,
    MySqlSqlExpressionFactory factory)
    : MySqlQueryTranslationPostprocessor(dependencies, relationalDependencies, queryCompilationContext, options, factory)
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