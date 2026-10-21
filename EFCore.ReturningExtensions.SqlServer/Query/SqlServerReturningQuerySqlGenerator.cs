using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.SqlServer.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;
using System.Reflection;

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
    private static readonly FieldInfo _withinTableField = typeof(SqlServerReturningQuerySqlGenerator).BaseType!.GetField("_withinTable",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
    
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
                GroupBy: [],
                Having: null,
                Projection: [],
                Orderings: [],
                Offset: null
            })
        {
            Sql.Append("UPDATE ");
            GenerateTop(selectExpression);

            Sql.AppendLine($"{Dependencies.SqlGenerationHelper.DelimitIdentifier(updateExpression.Table.Alias)}");
            Sql.Append("SET ");

            for (var i = 0; i < updateExpression.ColumnValueSetters.Count; i++)
            {
                var (column, value) = updateExpression.ColumnValueSetters[i];

                if (i == 1)
                {
                    Sql.IncrementIndent();
                }

                if (i > 0)
                {
                    Sql.AppendLine(",");
                }

                // SQL Server 2025 modify method (https://learn.microsoft.com/sql/t-sql/data-types/json-data-type#modify-method)
                // This requires special handling since modify isn't a standard setter of the form SET x = y, but rather just
                // SET [x].modify(...).
                if (value is SqlFunctionExpression
                    {
                        Name: "modify",
                        IsBuiltIn: true,
                        Instance: ColumnExpression { TypeMapping.StoreType: "json" } instance
                    })
                {
                    Visit(value);
                    continue;
                }

                Visit(column);
                Sql.Append(" = ");
                Visit(value);
            }

            if (updateExpression.ColumnValueSetters.Count > 1)
            {
                Sql.DecrementIndent();
            }

            Sql.AppendLine().Append("OUTPUT ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                var projection = expression.Projections[i];
                Visit(new TableAliasSelector("INSERTED", "DELETED").Visit(projection));
            }

            _withinTableField.SetValue(this, true);
            Sql.AppendLine().Append("FROM ");
            GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());
            _withinTableField.SetValue(this, false);

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

        if (selectExpression is
            {
                GroupBy: [],
                Having: null,
                Projection: [],
                Orderings: [],
                Offset: null
            })
        {
            Sql.Append("DELETE ");
            GenerateTop(selectExpression);

            _withinTableField.SetValue(this, true);
            Sql.AppendLine($"{Dependencies.SqlGenerationHelper.DelimitIdentifier(deleteExpression.Table.Alias)}");

            Sql.Append("OUTPUT ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                var projection = expression.Projections[i];
                Visit(new TableAliasSelector("DELETED", "DELETED").Visit(projection));
            }

            Sql.AppendLine().Append("FROM ");
            GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());
            _withinTableField.SetValue(this, false);

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