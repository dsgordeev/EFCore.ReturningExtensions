using System.Linq.Expressions;
using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.MySql.Expressions;
using EFCore.ReturningExtensions.MySql.Query.Extensions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure.Internal;
using Pomelo.EntityFrameworkCore.MySql.Query.ExpressionVisitors.Internal;

namespace EFCore.ReturningExtensions.MySql.Query;

internal class MySqlReturningQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource typeMappingSource,
    IMySqlOptions options)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
    {
        return new MySqlReturningQuerySqlGenerator(
            dependencies,
            typeMappingSource,
            options);
    }
}

public class MySqlReturningQuerySqlGenerator(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource typeMappingSource,
    IMySqlOptions options)
    : MySqlQuerySqlGenerator(dependencies, typeMappingSource, options)
{
    private const ulong LimitUpperBound = 18446744073709551610;

    protected override void GenerateRootCommand(Expression queryExpression)
    {
        if (queryExpression.ExtractReturningExpression() is { } returningExpression)
        {
            Visit(returningExpression);

            return;
        }

        base.GenerateRootCommand(queryExpression);
    }

    protected virtual Expression VisitReturningDelete(ReturningDeleteExpression expression)
    {
        var deleteExpression = expression.DeleteExpression;

        deleteExpression = (DeleteExpression)new MySqlUpdateConvertingExpressionVisitor().Process(deleteExpression);

        var selectExpression = deleteExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Having == null
            && selectExpression.GroupBy.Count == 0
            //&& selectExpression.Projection.Count == 0
            //&& (selectExpression.Tables.Count == 1 || selectExpression.Orderings.Count == 0 && selectExpression.Limit is null)
            )
        {
            Sql.Append("DELETE");

            Sql.AppendLine().Append("FROM ");

            Visit(deleteExpression.Table);
            GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");

                Visit(selectExpression.Predicate);
            }

            GenerateOrderings(selectExpression);
            GenerateLimitOffset(selectExpression);

            Sql.AppendLine().Append("RETURNING ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                var projection = expression.Projections[i];
                Visit(projection);
            }

            return deleteExpression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration("nameof(EntityFrameworkQueryableExtensions.ExecuteDelete)"));
    }

    protected virtual Expression VisitReturningUpdate(ReturningUpdateExpression expression)
    {
        var updateExpression = expression.UpdateExpression;

        updateExpression = (UpdateExpression)new MySqlUpdateConvertingExpressionVisitor().Process(updateExpression);

        var selectExpression = updateExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Having == null
            //&& selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            //&& selectExpression.Projection.Count == 0
            )
        {
            Sql.Append("UPDATE ");

            if (selectExpression.Tables.Count > 1)
            {
                var tables = selectExpression.Tables;

                if (selectExpression.Tables.All(t => !updateExpression.Table.Equals(t is JoinExpressionBase join ? join.Table : t)))
                {
                    Visit(updateExpression.Table);
                    Sql.AppendLine(",");

                    if (tables[0] is not JoinExpressionBase)
                    {
                        tables = tables
                            .Skip(1)
                            .Prepend(new CrossJoinExpression(tables[0]))
                            .ToArray();
                    }
                }

                GenerateList(tables, e => Visit(e), sql => sql.AppendLine());
            }
            else
            {
                Visit(updateExpression.Table);
            }

            var visitor = new TableAliasReplacer(updateExpression.Table.Alias);

            Sql.AppendLine().Append("SET ");
            Visit(visitor.Visit(updateExpression.ColumnValueSetters[0].Column));
            Sql.Append(" = ");
            Visit(visitor.Visit(updateExpression.ColumnValueSetters[0].Value));

            using (Sql.Indent())
            {
                foreach (var columnValueSetter in updateExpression.ColumnValueSetters.Skip(1))
                {
                    Sql.AppendLine(",");
                    Visit(visitor.Visit(columnValueSetter.Column));
                    Sql.Append(" = ");
                    Visit(visitor.Visit(columnValueSetter.Value));
                }
            }

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");
                Visit(selectExpression.Predicate);
            }

            GenerateOrderings(selectExpression);
            GenerateLimitOffset(selectExpression);

            Sql.AppendLine().Append("RETURNING ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                var projection = expression.Projections[i];
                Visit(projection);
            }

            return updateExpression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration("nameof(EntityFrameworkQueryableExtensions.ExecuteUpdate)"));
    }

    protected override Expression VisitColumn(ColumnExpression columnExpression)
    {
        if (columnExpression is DeletedColumnExpression)
        {
            Sql.Append("OLD_VALUE(");
            Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columnExpression.Name));
            Sql.Append(")");

            return columnExpression;
        }

        return base.VisitColumn(columnExpression);
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

    protected override void GenerateLimitOffset(SelectExpression selectExpression)
    {
        if (selectExpression.Limit != null)
        {
            Sql.AppendLine().Append("LIMIT ");
            Visit(selectExpression.Limit);
        }

        if (selectExpression.Offset != null)
        {
            if (selectExpression.Limit == null)
            {
                // if we want to use Skip() without Take() we have to define the upper limit of LIMIT
                Sql.AppendLine().Append($"LIMIT {LimitUpperBound}");
            }

            Sql.Append(" OFFSET ");
            Visit(selectExpression.Offset);
        }
    }

    public class MySqlUpdateConvertingExpressionVisitor : ExpressionVisitor
    {
        public virtual Expression Process(Expression node)
            => node switch
            {
                UpdateExpression updateExpression => VisitUpdate(updateExpression),
                DeleteExpression deleteExpression => VisitDelete(deleteExpression),

                _ => node
            };

        protected virtual Expression VisitUpdate(UpdateExpression updateExpression)
        {
            var select = VisitSelect(updateExpression.SelectExpression, updateExpression.Table.Alias, updateExpression.Table.Table);

            updateExpression = updateExpression.Update(select, updateExpression.ColumnValueSetters);

            return updateExpression;
        }

        protected virtual Expression VisitDelete(DeleteExpression deleteExpression)
        {
            var select = VisitSelect(deleteExpression.SelectExpression, deleteExpression.Table.Alias, deleteExpression.Table.Table);

            deleteExpression = deleteExpression.Update(select);

            return deleteExpression;
        }

        private SelectExpression VisitSelect(SelectExpression selectExpression, string targetTableAlias, ITableBase targetTable)
        {
            if (selectExpression.Offset != null
                || selectExpression.Having != null
                || selectExpression.GroupBy.Count > 0)
            {
                throw new InvalidOperationException(
                    RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(
                        "nameof(EntityFrameworkQueryableExtensions.ExecuteUpdate)"));
            }

            var fromItems = new List<TableExpressionBase>();
            SqlExpression? predicate = selectExpression.Predicate;
            SqlExpression? limitExpression = selectExpression.Limit;
            IReadOnlyList<OrderingExpression> orderingExpression = selectExpression.Orderings;

            foreach (var tableBase in selectExpression.Tables)
            {
                switch (tableBase)
                {
                    case TableExpression tableExpression:
                        if (tableExpression.Alias != targetTableAlias)
                        {
                            fromItems.Add(tableExpression);
                        }

                        break;

                    case InnerJoinExpression { Table: { } tableExpression } innerJoinExpression:
                        if (tableExpression.Alias != targetTableAlias)
                        {
                            if (tableExpression is SelectExpression s && s.Tables[0] is TableExpression t && t.Table == targetTable)
                            {
                                if (innerJoinExpression.JoinPredicate is SqlBinaryExpression { Left: ColumnExpression l, Right: ColumnExpression r, OperatorType: ExpressionType.Equal })
                                {
                                    if (l.Name == r.Name)
                                    {
                                        predicate = tableExpression is SelectExpression { Predicate: not null } s1
                                            ? predicate != null
                                                ? new SqlBinaryExpression(
                                                    ExpressionType.AndAlso,
                                                    predicate,
                                                     (SqlExpression)new TableAliasReplacer(targetTableAlias).Visit(s1.Predicate),
                                                    typeof(bool),
                                                    predicate.TypeMapping)
                                                : (SqlExpression)new TableAliasReplacer(targetTableAlias).Visit(s1.Predicate)
                                            : predicate;

                                        limitExpression = tableExpression is SelectExpression { Limit: not null } s2
                                            ? s2.Limit
                                            : limitExpression;

                                        orderingExpression = tableExpression is SelectExpression { Orderings.Count: > 0 } s3
                                            ? [.. orderingExpression, .. new TableAliasReplacer(targetTableAlias).Visit(s3.Orderings)]
                                            : orderingExpression;

                                        continue;
                                    }
                                }
                            }
                            else
                            {
                                fromItems.Add(tableExpression);
                            }

                            predicate = predicate is null
                                ? innerJoinExpression.JoinPredicate
                                : new SqlBinaryExpression(
                                    ExpressionType.AndAlso, predicate, innerJoinExpression.JoinPredicate,
                                    typeof(bool),
                                    innerJoinExpression.JoinPredicate.TypeMapping);
                        }
                        break;

                    default:
                        throw new InvalidOperationException(
                            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(
                                "nameof(EntityFrameworkQueryableExtensions.ExecuteDelete)"));
                }
            }

            selectExpression =
                selectExpression.Update(
                    selectExpression.Projection,
                    fromItems,
                    predicate,
                    selectExpression.GroupBy,
                    selectExpression.Having,
                    orderingExpression,
                    limitExpression,
                    selectExpression.Offset);

            return selectExpression;
        }
    }

    private class TableAliasReplacer(string x) : ExpressionVisitor
    {
        public IReadOnlyList<T> Visit<T>(IReadOnlyList<T> orderings) where T : Expression
        {
            return [.. orderings.Select(y => (T)Visit(y))];
        }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is DeletedColumnExpression deletedColumnExpression)
            {
                return new DeletedColumnExpression(
                    x,
                    deletedColumnExpression);
            }
            if (node is ColumnExpression columnExpression)
            {
                return new CustomTableAliasColumnExpression(
                    x,
                    columnExpression);
            }

            return base.VisitExtension(node);
        }
    }
}