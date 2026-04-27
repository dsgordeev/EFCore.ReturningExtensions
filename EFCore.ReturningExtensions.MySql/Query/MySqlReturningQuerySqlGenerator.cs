using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Microting.Expressions;
using EFCore.ReturningExtensions.Microting.Query.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure.Internal;
using Pomelo.EntityFrameworkCore.MySql.Query.ExpressionVisitors.Internal;
using System.Linq.Expressions;

namespace EFCore.ReturningExtensions.Microting.Query;

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

    private string _removeTableAliasOld;
    private string _removeTableAliasNew;

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

        var selectExpression = deleteExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Having == null
            && selectExpression.GroupBy.Count == 0
            //&& selectExpression.Projection.Count == 0
            && (selectExpression.Tables.Count == 1 || selectExpression.Orderings.Count == 0 && selectExpression.Limit is null))
        {
            var removeSingleTableAlias = selectExpression.Tables.Count == 1 &&
                                         selectExpression.Orderings.Count > 0 || selectExpression.Limit is not null;

            removeSingleTableAlias = true;

            Sql.Append("DELETE");

            if (!removeSingleTableAlias)
            {
                Sql.Append($" {Dependencies.SqlGenerationHelper.DelimitIdentifier(deleteExpression.Table.Alias)}");
            }

            Sql.AppendLine().Append("FROM ");

            if (removeSingleTableAlias)
            {
                _removeTableAliasOld = selectExpression.Tables[0].Alias!;
                _removeTableAliasNew = null!;
            }

            GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");

                var predicate = selectExpression.Predicate switch
                {
                    InExpression { Subquery.Predicate: { } p } => new TableAliasReplacer(deleteExpression.Table.Alias).Visit(p),
                    _ => selectExpression.Predicate
                };

                Visit(predicate);
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

            if (removeSingleTableAlias)
            {
                _removeTableAliasOld = null!;
            }

            return deleteExpression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(nameof(EntityFrameworkQueryableExtensions.ExecuteDelete)));
    }

    protected virtual Expression VisitReturningUpdate(ReturningUpdateExpression expression)
    {
        var updateExpression = expression.UpdateExpression;

        var selectExpression = updateExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            //&& selectExpression.Projection.Count == 0
            )
        {
            Sql.Append("UPDATE ");
            Visit(updateExpression.Table);
            Sql.AppendLine().Append("SET ");
            Visit(updateExpression.ColumnValueSetters[0].Column);
            Sql.Append(" = ");
            Visit(new TableAliasReplacer(updateExpression.Table.Alias).Visit(updateExpression.ColumnValueSetters[0].Value));

            using (Sql.Indent())
            {
                foreach (var columnValueSetter in updateExpression.ColumnValueSetters.Skip(1))
                {
                    Sql.AppendLine(",");
                    Visit(columnValueSetter.Column);
                    Sql.Append(" = ");
                    Visit(new TableAliasReplacer(updateExpression.Table.Alias).Visit(columnValueSetter.Value));
                }
            }

            if (selectExpression.Tables.Count > 1)
            {
                var select = selectExpression switch
                {
                    var x when x.Tables[1] is InnerJoinExpression { Table: SelectExpression innerSelect } => (SelectExpression)new TableAliasReplacer(updateExpression.Table.Alias).Visit(innerSelect),
                    _ => selectExpression
                };

                var predicate = select.Predicate switch
                {
                    { } x => x,
                    _ => selectExpression.Predicate
                };

                if (predicate != null)
                {
                    Sql.AppendLine().Append("WHERE ");
                    Visit(new TableAliasReplacer(updateExpression.Table.Alias).Visit(predicate));
                }

                GenerateOrderings(select);
                GenerateLimitOffset(select);
            }
            else
            {
                if (selectExpression.Predicate != null)
                {
                    Sql.AppendLine().Append("WHERE ");
                    Visit(selectExpression.Predicate);
                }

                GenerateOrderings(selectExpression);
                GenerateLimitOffset(selectExpression);
            }

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
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(nameof(EntityFrameworkQueryableExtensions.ExecuteUpdate)));
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

    private class TableAliasReplacer(string x) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is DeletedColumnExpression deletedColumnExpression)
            {
                return new DeletedColumnExpression(
                    deletedColumnExpression.Name,
                    x,
                    deletedColumnExpression.Type,
                    deletedColumnExpression.TypeMapping,
                    deletedColumnExpression.IsNullable);
            }
            if (node is ColumnExpression columnExpression)
            {
                return new CustomTableAliasColumnExpression(
                    x,
                    columnExpression);
            }

            return base.VisitExtension(node);
        }

        private class CustomTableAliasColumnExpression(string tableAlias, ColumnExpression columnExpression)
            : ColumnExpression(
                columnExpression.Name,
                tableAlias,
                columnExpression.Type,
                columnExpression.TypeMapping,
                columnExpression.IsNullable);
    }
}