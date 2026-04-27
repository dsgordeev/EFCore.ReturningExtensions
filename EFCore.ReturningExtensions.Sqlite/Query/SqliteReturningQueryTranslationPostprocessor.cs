using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.Sqlite.Query;

internal class SqliteReturningQueryTranslationPostprocessorFactory(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies)
    : IQueryTranslationPostprocessorFactory
{
    [DebuggerStepThrough]
    public virtual QueryTranslationPostprocessor Create(QueryCompilationContext queryCompilationContext)
        => new SqliteReturningQueryTranslationPostprocessor(
            dependencies, relationalDependencies, queryCompilationContext);
}

public class SqliteReturningQueryTranslationPostprocessor(
    QueryTranslationPostprocessorDependencies dependencies,
    RelationalQueryTranslationPostprocessorDependencies relationalDependencies,
    QueryCompilationContext queryCompilationContext)
    : SqliteQueryTranslationPostprocessor(dependencies, relationalDependencies, queryCompilationContext)
{
    public override Expression Process(Expression query)
    {
        if (query is ShapedQueryExpression shaped)
        {
            if (shaped.QueryExpression is ReturningUpdateExpression returningUpdate)
            {
                var projectionLessSelect = returningUpdate.UpdateExpression.SelectExpression.Update(
                    [],
                    returningUpdate.UpdateExpression.SelectExpression.Tables,
                    returningUpdate.UpdateExpression.SelectExpression.Predicate,
                    returningUpdate.UpdateExpression.SelectExpression.GroupBy,
                    returningUpdate.UpdateExpression.SelectExpression.Having,
                    returningUpdate.UpdateExpression.SelectExpression.Orderings,
                    returningUpdate.UpdateExpression.SelectExpression.Limit,
                    returningUpdate.UpdateExpression.SelectExpression.Offset
                ).Clone();

                var select = (SelectExpression)base.Process(projectionLessSelect);

                var f1 = typeof(SelectExpression).GetField("_projection", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                var f2 = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                var f3 = typeof(SelectExpression).GetField("_identifier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

                f1.SetValue(select, f1.GetValue(returningUpdate.UpdateExpression.SelectExpression));
                f2.SetValue(select, f2.GetValue(returningUpdate.UpdateExpression.SelectExpression));
                f3.SetValue(select, f3.GetValue(returningUpdate.UpdateExpression.SelectExpression));

                var update = returningUpdate.UpdateExpression.Update(select, returningUpdate.UpdateExpression.ColumnValueSetters);

                return shaped.UpdateQueryExpression(returningUpdate.Update(update, returningUpdate.Projections));
            }

            if (shaped.QueryExpression is ReturningDeleteExpression returningDelete)
            {
                var projectionLessSelect = returningDelete.DeleteExpression.SelectExpression.Update(
                    [],
                    returningDelete.DeleteExpression.SelectExpression.Tables,
                    returningDelete.DeleteExpression.SelectExpression.Predicate,
                    returningDelete.DeleteExpression.SelectExpression.GroupBy,
                    returningDelete.DeleteExpression.SelectExpression.Having,
                    returningDelete.DeleteExpression.SelectExpression.Orderings,
                    returningDelete.DeleteExpression.SelectExpression.Limit,
                    returningDelete.DeleteExpression.SelectExpression.Offset
                ).Clone();

                var select = (SelectExpression)base.Process(projectionLessSelect);

                var f1 = typeof(SelectExpression).GetField("_projection", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                var f2 = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                var f3 = typeof(SelectExpression).GetField("_identifier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

                f1.SetValue(select, f1.GetValue(returningDelete.DeleteExpression.SelectExpression));
                f2.SetValue(select, f2.GetValue(returningDelete.DeleteExpression.SelectExpression));
                f3.SetValue(select, f3.GetValue(returningDelete.DeleteExpression.SelectExpression));

                var delete = returningDelete.DeleteExpression.Update(select);

                return shaped.UpdateQueryExpression(returningDelete.Update(delete, returningDelete.Projections));
            }
        }

        return base.Process(query);
    }
}