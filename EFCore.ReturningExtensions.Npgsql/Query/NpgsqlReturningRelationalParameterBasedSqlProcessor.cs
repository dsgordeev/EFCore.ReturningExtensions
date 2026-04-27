using System.Linq.Expressions;
using System.Reflection;
using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace EFCore.ReturningExtensions.Npgsql.Query;

public class NpgsqlReturningRelationalParameterBasedSqlProcessorFactory(RelationalParameterBasedSqlProcessorDependencies dependencies)
    : IRelationalParameterBasedSqlProcessorFactory
{
    protected virtual RelationalParameterBasedSqlProcessorDependencies Dependencies { get; } = dependencies;

    public virtual RelationalParameterBasedSqlProcessor Create(bool useRelationalNulls)
        => new NpgsqlReturningRelationalParameterBasedSqlProcessor(Dependencies, useRelationalNulls);
}
public class NpgsqlReturningRelationalParameterBasedSqlProcessor(
    RelationalParameterBasedSqlProcessorDependencies dependencies,
    bool useRelationalNulls)
    : NpgsqlParameterBasedSqlProcessor(dependencies, useRelationalNulls)
{
    protected override Expression ProcessSqlNullability(
        Expression selectExpression,
        IReadOnlyDictionary<string, object?> parametersValues,
        out bool canCache)
    {
        return new NpgsqlReturningNpgsqlSqlNullabilityProcessor(Dependencies, UseRelationalNulls).Process(
            selectExpression, parametersValues, out canCache);
    }

    public class NpgsqlReturningNpgsqlSqlNullabilityProcessor(
        RelationalParameterBasedSqlProcessorDependencies dependencies,
        bool useRelationalNulls)
        : NpgsqlSqlNullabilityProcessor(dependencies, useRelationalNulls)
    {
        public override Expression Process(Expression queryExpression, IReadOnlyDictionary<string, object?> parameterValues, out bool canCache)
        {
            if (queryExpression is ReturningUpdateExpression returningUpdate)
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

                var update = (UpdateExpression)base.Process(
                    returningUpdate.UpdateExpression.Update(projectionLessSelect, returningUpdate.UpdateExpression.ColumnValueSetters), 
                    parameterValues, 
                    out canCache);

                var f1 = typeof(SelectExpression).GetField("_projection", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                var f2 = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                var f3 = typeof(SelectExpression).GetField("_identifier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

                f1.SetValue(update.SelectExpression, f1.GetValue(returningUpdate.UpdateExpression.SelectExpression));
                f2.SetValue(update.SelectExpression, f2.GetValue(returningUpdate.UpdateExpression.SelectExpression));
                f3.SetValue(update.SelectExpression, f3.GetValue(returningUpdate.UpdateExpression.SelectExpression));

                return returningUpdate.Update(update, returningUpdate.Projections);
            }

            if (queryExpression is ReturningDeleteExpression returningDelete)
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

                var delete = (DeleteExpression)base.Process(
                    returningDelete.DeleteExpression.Update(projectionLessSelect), 
                    parameterValues, 
                    out canCache);

                var f1 = typeof(SelectExpression).GetField("_projection", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                var f2 = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                var f3 = typeof(SelectExpression).GetField("_identifier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

                f1.SetValue(delete.SelectExpression, f1.GetValue(returningDelete.DeleteExpression.SelectExpression));
                f2.SetValue(delete.SelectExpression, f2.GetValue(returningDelete.DeleteExpression.SelectExpression));
                f3.SetValue(delete.SelectExpression, f3.GetValue(returningDelete.DeleteExpression.SelectExpression));

                return returningDelete.Update(delete, returningDelete.Projections);
            }

            return base.Process(queryExpression, parameterValues, out canCache);
        }
    }
}