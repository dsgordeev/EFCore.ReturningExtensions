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
    [DebuggerStepThrough]
    public QueryTranslationPostprocessor Create(QueryCompilationContext queryCompilationContext)
        => new SqliteReturningQueryTranslationPostprocessor(
            dependencies, relationalDependencies, (SqliteQueryCompilationContext)queryCompilationContext);
}

public class SqliteReturningQueryTranslationPostprocessor(
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
                var update = (UpdateExpression)base.Process(returningUpdate.UpdateExpression);
                
                return shaped.UpdateQueryExpression(returningUpdate.Update(update, returningUpdate.Projections));
            }

            if (shaped.QueryExpression is ReturningDeleteExpression returningDelete)
            {
                var delete = (DeleteExpression)base.Process(returningDelete.DeleteExpression);

                return shaped.UpdateQueryExpression(returningDelete.Update(delete, returningDelete.Projections));
            }
        }

        return base.Process(query);
    }
}