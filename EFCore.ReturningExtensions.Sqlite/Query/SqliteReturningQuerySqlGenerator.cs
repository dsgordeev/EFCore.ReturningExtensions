using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Sqlite.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;

namespace EFCore.ReturningExtensions.Sqlite.Query;

internal class SqliteReturningQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
    {
        return new SqliteReturningQuerySqlGenerator(dependencies);
    }
}

internal class SqliteReturningQuerySqlGenerator(
    QuerySqlGeneratorDependencies dependencies)
    : SqliteQuerySqlGenerator(dependencies)
{
    protected override void GenerateRootCommand(Expression queryExpression)
    {
        if (queryExpression.ExtractReturningExpression() is { } returningExpression)
        {
            Visit(returningExpression);

            return;
        }

        base.GenerateRootCommand(queryExpression);
    }

    protected Expression VisitReturningUpdate(ReturningUpdateExpression expression)
    {
        var updateExpression = expression.UpdateExpression;

        var selectExpression = updateExpression.SelectExpression;

        if (selectExpression is
            {
                Offset: null,
                Limit: null,
                Having: null,
                Orderings: [],
                GroupBy: [],
                Projection: [],
            }
            && (selectExpression.Tables.Count == 1
                || !ReferenceEquals(selectExpression.Tables[0], updateExpression.Table)
                || selectExpression.Tables[1] is InnerJoinExpression
                || selectExpression.Tables[1] is CrossJoinExpression))
        {
            Sql.Append("UPDATE ");

            Visit(updateExpression.Table);

            Sql.AppendLine();
            Sql.Append("SET ");

            for (var i = 0; i < updateExpression.ColumnValueSetters.Count; i++)
            {
                if (i == 1)
                {
                    Sql.IncrementIndent();
                }

                if (i > 0)
                {
                    Sql.AppendLine(",");
                }

                var (column, value) = updateExpression.ColumnValueSetters[i];

                Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(column.Name)).Append(" = ");
                Visit(value);
            }

            if (updateExpression.ColumnValueSetters.Count > 1)
            {
                Sql.DecrementIndent();
            }

            var predicate = selectExpression.Predicate;
            var firstTablePrinted = false;
            if (selectExpression.Tables.Count > 1)
            {
                Sql.AppendLine().Append("FROM ");
                for (var i = 0; i < selectExpression.Tables.Count; i++)
                {
                    var table = selectExpression.Tables[i];
                    var joinExpression = table as JoinExpressionBase;

                    if (updateExpression.Table.Alias == (joinExpression?.Table.Alias ?? table.Alias))
                    {
                        LiftPredicate(table);
                        continue;
                    }

                    if (firstTablePrinted)
                    {
                        Sql.AppendLine();
                    }
                    else
                    {
                        firstTablePrinted = true;
                        LiftPredicate(table);
                        table = joinExpression?.Table ?? table;
                    }

                    Visit(table);

                    void LiftPredicate(TableExpressionBase joinTable)
                    {
                        if (joinTable is PredicateJoinExpressionBase predicateJoinExpression)
                        {
                            predicate = predicate == null
                                ? predicateJoinExpression.JoinPredicate
                                : new SqlBinaryExpression(
                                    ExpressionType.AndAlso,
                                    predicateJoinExpression.JoinPredicate,
                                    predicate,
                                    typeof(bool),
                                    predicate.TypeMapping);
                        }
                    }
                }
            }

            if (predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");
                Visit(predicate);
            }

            Sql.AppendLine().Append("RETURNING ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                Visit(new TableAliasCleaner().Visit(expression.Projections[i].Expression));
            }

            return updateExpression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(
                nameof(EntityFrameworkQueryableExtensions.ExecuteUpdate)));
    }

    protected Expression VisitReturningDelete(ReturningDeleteExpression expression)
    {
        var deleteExpression = expression.DeleteExpression;

        var selectExpression = deleteExpression.SelectExpression;

        if (selectExpression is
            {
                Tables: [var table],
                GroupBy: [],
                Having: null,
                Projection: [],
                Orderings: [],
                Offset: null,
                Limit: null
            }
            && table.Equals(deleteExpression.Table))
        {
            Sql.Append("DELETE FROM ");
            Visit(deleteExpression.Table);

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");
                Visit(selectExpression.Predicate);
            }

            Sql.AppendLine().Append("RETURNING ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                var projection = expression.Projections[i];
                Visit(new TableAliasCleaner().Visit(projection));
            }

            return deleteExpression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(
                nameof(EntityFrameworkQueryableExtensions.ExecuteDelete)));
    }

    protected override Expression VisitColumn(ColumnExpression columnExpression)
    {
        if (columnExpression.TableAlias.Length > 0)
        {
            Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columnExpression.TableAlias));
            Sql.Append(".");
        }

        Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columnExpression.Name));

        return columnExpression;
    }

    protected override Expression VisitExtension(Expression extension)
    {
        if (extension is ReturningUpdateExpression outputUpdate)
        {
            return VisitReturningUpdate(outputUpdate);
        }
        if (extension is ReturningDeleteExpression outputDelete)
        {
            return VisitReturningDelete(outputDelete);
        }

        return base.VisitExtension(extension);
    }

    private void GenerateList<T>(
        IReadOnlyList<T> items,
        Action<T> generationAction,
        Action<IRelationalCommandBuilder>? joinAction = null)
    {
        joinAction ??= (isb => isb.Append(", "));

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                joinAction(Sql);
            }

            generationAction(items[i]);
        }
    }

    private class AliasFreeColumnExpression(ColumnExpression columnExpression)
        : ColumnExpression(
            columnExpression.Name,
            string.Empty,
            columnExpression.Type,
            columnExpression.TypeMapping,
            columnExpression.IsNullable);

    private class TableAliasCleaner : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is ColumnExpression columnExpression)
            {
                return new AliasFreeColumnExpression(columnExpression);
            }

            return base.VisitExtension(node);
        }
    }
}