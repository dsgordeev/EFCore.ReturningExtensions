using System.Linq.Expressions;
using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.SqlServer.Extensions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ReturningExtensions.SqlServer.Query;

internal class SqlServerReturningQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource typeMappingSource,
    ISqlServerSingletonOptions sqlServerSingletonOptions)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
    {
        return new SqlServerReturningQuerySqlGenerator(dependencies, typeMappingSource, sqlServerSingletonOptions);
    }
}

internal class SqlServerReturningQuerySqlGenerator(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource typeMappingSource,
    ISqlServerSingletonOptions sqlServerSingletonOptions)
    : SqlServerQuerySqlGenerator(dependencies, typeMappingSource, sqlServerSingletonOptions)
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

        if (selectExpression.Offset == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Projection.Count == 0)
        {
            Sql.Append("UPDATE ");
            GenerateTop(selectExpression);

            Sql.AppendLine($"{Dependencies.SqlGenerationHelper.DelimitIdentifier(updateExpression.Table.Alias)}");
            Sql.Append("SET ");
            Visit(updateExpression.ColumnValueSetters[0].Column);
            Sql.Append(" = ");
            Visit(updateExpression.ColumnValueSetters[0].Value);

            using (Sql.Indent())
            {
                foreach (var columnValueSetter in updateExpression.ColumnValueSetters.Skip(1))
                {
                    Sql.AppendLine(",");
                    Visit(columnValueSetter.Column);
                    Sql.Append(" = ");
                    Visit(columnValueSetter.Value);
                }
            }

            Sql.AppendLine().Append("OUTPUT ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                Sql.Append("INSERTED.");
                Visit(new TableAliasCleaner().Visit(expression.Projections[i].Expression));
            }

            Sql.AppendLine().Append("FROM ");
            GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");
                Visit(selectExpression.Predicate);
            }

            return expression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(ReturningUpdateExpressionExtensions.InternalUpdatedMethodInfo.Name));
    }

    protected Expression VisitReturningDelete(ReturningDeleteExpression expression)
    {
        var deleteExpression = expression.DeleteExpression;

        var selectExpression = deleteExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Projection.Count == 0)
        {
            Sql.Append("DELETE ");
            GenerateTop(selectExpression);
            Sql.Append($"{Dependencies.SqlGenerationHelper.DelimitIdentifier(deleteExpression.Table.Alias)}");

            Sql.AppendLine().Append("OUTPUT ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                Sql.Append("DELETED.");
                Visit(new TableAliasCleaner().Visit(expression.Projections[i].Expression));
            }

            Sql.AppendLine();

            Sql.Append("FROM ");
            GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");

                Visit(selectExpression.Predicate);
            }

            GenerateLimitOffset(selectExpression);

            return expression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(ReturningDeleteExpressionExtensions.InternalDeletedMethodInfo.Name));
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