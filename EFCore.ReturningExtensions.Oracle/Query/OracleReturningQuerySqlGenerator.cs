using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Oracle.Expressions;
using EFCore.ReturningExtensions.Oracle.Query.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Oracle.EntityFrameworkCore.Infrastructure.Internal;
using Oracle.EntityFrameworkCore.Query.Sql.Internal;
using System.Linq.Expressions;

namespace EFCore.ReturningExtensions.Oracle.Query;

internal class OracleReturningQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource typeMappingSource,
    IOracleOptions oracleOptions)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
    {
        return new OracleReturningQuerySqlGenerator(dependencies, typeMappingSource, oracleOptions.OracleSQLCompatibility);
    }
}

internal class OracleReturningQuerySqlGenerator(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource typeMappingSource,
    OracleSQLCompatibility oracleSqlCompatibility)
    : OracleQuerySqlGenerator(dependencies, typeMappingSource, oracleSqlCompatibility)
{
    private readonly OracleSQLCompatibility _oracleSqlCompatibility = oracleSqlCompatibility;

    protected override void GenerateRootCommand(Expression queryExpression)
    {
        if (queryExpression.ExtractReturningExpression() is { } returningExpression)
        {
            GenerateTagsHeaderComment(((SelectExpression)queryExpression).Tags);
            Visit(returningExpression);

            return;
        }

        base.GenerateRootCommand(queryExpression);
    }

    protected Expression VisitReturningUpdate(ReturningUpdateExpression expression)
    {
        var updateExpression = expression.UpdateExpression;

        var selectExpression = updateExpression.SelectExpression;

        if (selectExpression.Offset != null || selectExpression.Limit != null || selectExpression.Having != null || selectExpression.Orderings.Count != 0 || selectExpression.GroupBy.Count != 0 || selectExpression.Projection.Count != 0 || selectExpression.Tables.Count != 1 && selectExpression.Tables[0] == updateExpression.Table && !(selectExpression.Tables[1] is InnerJoinExpression) && !(selectExpression.Tables[1] is CrossJoinExpression))
        {
            throw new InvalidOperationException(
                RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration((object)"ExecuteUpdate"));
        }
        if (_oracleSqlCompatibility < OracleSQLCompatibility.DatabaseVersion23)
        {
            if (selectExpression.Tables.Count > 1)
            {
                Sql.Append($"UPDATE ( SELECT {Dependencies.SqlGenerationHelper.DelimitIdentifier(updateExpression.Table.Alias)}.* FROM ");
                GenerateList(selectExpression.Tables, (e => Visit(e)), (Action<IRelationalCommandBuilder>)(sql => sql.AppendLine()));
                if (selectExpression.Predicate != null)
                {
                    Sql.AppendLine().Append("WHERE ");
                    Visit(selectExpression.Predicate);
                }
                Sql.Append(" ) ");
                Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(updateExpression.Table.Alias) ?? "");
                Sql.AppendLine();
                Sql.Append("SET ");
                ColumnValueSetter columnValueSetter1 = updateExpression.ColumnValueSetters[0];
                Visit(columnValueSetter1.Column);
                Sql.Append(" = ");
                columnValueSetter1 = updateExpression.ColumnValueSetters[0];
                Visit(columnValueSetter1.Value);
                using (Sql.Indent())
                {
                    foreach (ColumnValueSetter columnValueSetter2 in updateExpression.ColumnValueSetters.Skip<ColumnValueSetter>(1))
                    {
                        Sql.AppendLine(",");
                        Visit(columnValueSetter2.Column);
                        Sql.Append(" = ");
                        Visit(columnValueSetter2.Value);
                    }
                }

                Sql.AppendLine().Append("RETURNING ");
                for (var i = 0; i < expression.Projections.Count; i++)
                {
                    if (i > 0) Sql.Append(", ");
                    var projection = expression.Projections[i];
                    Visit(new TableAliasSelector(updateExpression.Table.Alias, "OLD").Visit(projection));
                }

                return updateExpression;
            }
            Sql.Append("UPDATE ");
            Visit(updateExpression.Table);
            Sql.AppendLine();
            Sql.Append("SET ");
            Visit(updateExpression.ColumnValueSetters[0].Column);
            Sql.Append(" = ");
            Visit(updateExpression.ColumnValueSetters[0].Value);
            using (Sql.Indent())
            {
                foreach (ColumnValueSetter columnValueSetter in updateExpression.ColumnValueSetters.Skip<ColumnValueSetter>(1))
                {
                    Sql.AppendLine(",");
                    Visit(columnValueSetter.Column);
                    Sql.Append(" = ");
                    Visit(columnValueSetter.Value);
                }
            }
            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");
                Visit(selectExpression.Predicate);
            }
            return updateExpression;
        }
        if (_oracleSqlCompatibility >= OracleSQLCompatibility.DatabaseVersion23)
        {
            Sql.Append("UPDATE ");
            Visit(updateExpression.Table);
            Sql.AppendLine();
            Sql.Append("SET ");
            Visit(updateExpression.ColumnValueSetters[0].Column);
            Sql.Append(" = ");
            Visit(updateExpression.ColumnValueSetters[0].Value);
            using (Sql.Indent())
            {
                foreach (ColumnValueSetter columnValueSetter in updateExpression.ColumnValueSetters.Skip<ColumnValueSetter>(1))
                {
                    Sql.AppendLine(",");
                    Visit(columnValueSetter.Column);
                    Sql.Append(" = ");
                    Visit(columnValueSetter.Value);
                }
            }
            SqlExpression predicate = selectExpression.Predicate!;
            bool flag = false;
            if (selectExpression.Tables.Count > 1)
            {
                Sql.AppendLine().Append("FROM ");
                for (int index = 0; index < selectExpression.Tables.Count; ++index)
                {
                    TableExpressionBase table = selectExpression.Tables[index];
                    JoinExpressionBase? joinExpressionBase = table as JoinExpressionBase;
                    if (updateExpression.Table.Equals(joinExpressionBase?.Table ?? table))
                    {
                        LiftPredicate(table);
                    }
                    else
                    {
                        TableExpressionBase node;
                        if (flag && selectExpression.Tables.Count > 2)
                        {
                            Sql.Append(", ");
                            LiftPredicate(table);
                            node = joinExpressionBase?.Table ?? table;
                        }
                        else
                        {
                            flag = true;
                            LiftPredicate(table);
                            node = joinExpressionBase?.Table ?? table;
                        }
                        Visit(node);
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
                var projection = expression.Projections[i];
                Visit(new TableAliasSelector(updateExpression.Table.Alias, "OLD").Visit(projection));
            }

            void LiftPredicate(TableExpressionBase joinTable)
            {
                if (!(joinTable is PredicateJoinExpressionBase joinExpressionBase1))
                    return;
                predicate = predicate == null ? joinExpressionBase1.JoinPredicate : (SqlExpression)new SqlBinaryExpression(ExpressionType.AndAlso, joinExpressionBase1.JoinPredicate, predicate, typeof(bool), predicate.TypeMapping);
            }
        }
        return updateExpression;
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
                Visit(new TableAliasSelector(deleteExpression.Table.Alias, "OLD").Visit(projection));
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

    private class CustomTableAliasColumnExpression(string tableAlias, ColumnExpression columnExpression)
        : ColumnExpression(
            columnExpression.Name,
            tableAlias,
            columnExpression.Type,
            columnExpression.TypeMapping,
            columnExpression.IsNullable);
    
    private class TableAliasSelector(string x, string y) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is DeletedColumnExpression deletedColumnExpression)
            {
                return new CustomTableAliasColumnExpression(
                    y,
                    deletedColumnExpression);
            }
            else if (node is ColumnExpression columnExpression)
            {
                return new CustomTableAliasColumnExpression(
                    x,
                    columnExpression);
            }

            return base.VisitExtension(node);
        }
    }
}