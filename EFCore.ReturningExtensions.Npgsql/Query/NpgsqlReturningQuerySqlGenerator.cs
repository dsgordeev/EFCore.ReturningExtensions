using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Npgsql.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.Mapping;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Npgsql.Query.Extensions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFCore.ReturningExtensions.Npgsql.Query;

internal class NpgsqlReturningQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource typeMappingSource,
    INpgsqlSingletonOptions npgsqlSingletonOptions)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
    {
        return new NpgsqlReturningQuerySqlGenerator(
            dependencies,
            typeMappingSource,
            npgsqlSingletonOptions.ReverseNullOrderingEnabled,
            npgsqlSingletonOptions.PostgresVersion);
    }
}

internal class NpgsqlReturningQuerySqlGenerator(
    QuerySqlGeneratorDependencies dependencies,
    IRelationalTypeMappingSource typeMappingSource,
    bool reverseNullOrderingEnabled,
    Version postgresVersion)
    : NpgsqlQuerySqlGenerator(
        dependencies,
        typeMappingSource,
        reverseNullOrderingEnabled,
        postgresVersion)
{
    private readonly Version _postgresVersion = postgresVersion;

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
            && selectExpression.Limit == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Projection.Count == 0
            && (selectExpression.Tables.Count == 1
                || !ReferenceEquals(selectExpression.Tables[0], updateExpression.Table)
                || selectExpression.Tables[1] is InnerJoinExpression
                || selectExpression.Tables[1] is CrossJoinExpression))
        {
            Sql.Append("UPDATE ");
            Visit(updateExpression.Table);
            Sql.AppendLine();
            Sql.Append("SET ");
            Sql.Append(
                $"{Dependencies.SqlGenerationHelper.DelimitIdentifier(updateExpression.ColumnValueSetters[0].Column.Name)} = ");
            Visit(updateExpression.ColumnValueSetters[0].Value);
            using (Sql.Indent())
            {
                foreach (var columnValueSetter in updateExpression.ColumnValueSetters.Skip(1))
                {
                    Sql.AppendLine(",");
                    Sql.Append($"{Dependencies.SqlGenerationHelper.DelimitIdentifier(columnValueSetter.Column.Name)} = ");
                    Visit(columnValueSetter.Value);
                }
            }

            var predicate = selectExpression.Predicate;
            var firstTable = true;
            OuterReferenceFindingExpressionVisitor? visitor = null;

            List<TableExpressionBase> tables = [.. selectExpression.Tables];

            if (_postgresVersion.Major <= 17)
            {
                var tableAlias = tables.LastOrDefault() is JoinExpressionBase jsonExpression
                    ? jsonExpression.Table.Alias!
                    : selectExpression.Tables[0].Alias!;

                var entityType = updateExpression.Table.Table.EntityTypeMappings
                    .Select(m => m.TypeBase.ContainingEntityType)
                    .FirstOrDefault()!.ClrType;

                var model = updateExpression.Table.Table.Model.Model;
                var keys = model.FindEntityType(entityType)!.FindPrimaryKey()!.Properties;

                var joinPredicate = (
                    from key in keys 
                    let left = new ColumnExpression(key.Name, tableAlias, key.ClrType, key.GetRelationalTypeMapping(), key.IsNullable) 
                    let right = new ColumnExpression(key.Name, "OLD", key.ClrType, key.GetRelationalTypeMapping(), key.IsNullable) 
                    select new SqlBinaryExpression(ExpressionType.Equal, left, right, typeof(bool), NpgsqlBoolTypeMapping.Default))
                    .Aggregate<SqlBinaryExpression, SqlBinaryExpression?>(null, (current, equality) => 
                        current == null
                        ? equality
                        : new SqlBinaryExpression(ExpressionType.Equal, current, equality, typeof(bool), NpgsqlBoolTypeMapping.Default));

                var sqlAliasManagerField = typeof(SelectExpression).GetField("_sqlAliasManager",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField);
                var sqlAliasManager = (SqlAliasManager)sqlAliasManagerField!.GetValue(updateExpression.SelectExpression)!;

                tables.Add(new InnerJoinExpression(
                    updateExpression.SelectExpression
                        .Tables[0]
                        .Clone("OLD", new CloningExpressionVisitor(sqlAliasManager)),
                    joinPredicate!
                ));
            }

            if (tables.Count > 1)
            {
                Sql.AppendLine().Append("FROM ");

                for (var i = 0; i < tables.Count; i++)
                {
                    var table = tables[i];
                    var joinExpression = table as JoinExpressionBase;

                    if (updateExpression.Table.Alias == (joinExpression?.Table.Alias ?? table.Alias))
                    {
                        LiftPredicate(table);
                        continue;
                    }

                    visitor ??= new OuterReferenceFindingExpressionVisitor(updateExpression.Table);

                    // PostgreSQL doesn't support referencing the main update table from anywhere except for the UPDATE WHERE clause.
                    // This specifically makes it impossible to have joins which reference the main table in their predicate (ON ...).
                    // Because of this, we detect all such inner joins and lift their predicates to the main WHERE clause (where a reference to the
                    // main table is allowed), producing UPDATE ... FROM x, y WHERE y.foreign_key = x.id instead of INNER JOIN ... ON.
                    if (firstTable)
                    {
                        LiftPredicate(table);
                        table = joinExpression?.Table ?? table;
                    }
                    else if (joinExpression is InnerJoinExpression innerJoinExpression
                             && visitor.ContainsReferenceToMainTable(innerJoinExpression.JoinPredicate))
                    {
                        LiftPredicate(innerJoinExpression);

                        Sql.AppendLine(",");
                        using (Sql.Indent())
                        {
                            Visit(innerJoinExpression.Table);
                        }

                        continue;
                    }

                    if (firstTable)
                    {
                        firstTable = false;
                    }
                    else
                    {
                        Sql.AppendLine();
                    }

                    Visit(table);

                    void LiftPredicate(TableExpressionBase joinTable)
                    {
                        if (joinTable is PredicateJoinExpressionBase predicateJoinExpression)
                        {
                            //Check.DebugAssert(joinExpression is not LeftJoinExpression, "Cannot lift predicate for left join");

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
                var projection = expression.Projections[i];
                Visit(new TableAliasSelector(updateExpression.Table.Alias, "OLD").Visit(projection));
            }

            return expression;
        }

        throw new InvalidOperationException(
            RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(ReturningUpdateExpressionExtensions.InternalUpdatedMethodInfo.Name));
    }

    protected Expression VisitReturningDelete(ReturningDeleteExpression expression)
    {
        var pgDeleteExpression = (PgDeleteExpression)
            new NpgsqlDeleteConvertingExpressionVisitor().Process(expression.DeleteExpression);

        Sql.Append("DELETE FROM ");
        Visit(pgDeleteExpression.Table);

        if (pgDeleteExpression.FromItems.Count > 0)
        {
            Sql.AppendLine().Append("USING ");
            GenerateList(pgDeleteExpression.FromItems, t => Visit(t), sql => sql.Append(", "));
        }

        if (pgDeleteExpression.Predicate != null)
        {
            Sql.AppendLine().Append("WHERE ");
            Visit(pgDeleteExpression.Predicate);
        }

        Sql.AppendLine().Append("RETURNING ");
        for (var i = 0; i < expression.Projections.Count; i++)
        {
            if (i > 0) Sql.Append(", ");
            var projection = expression.Projections[i];
            Visit(new TableAliasSelector(pgDeleteExpression.Table.Alias, "OLD").Visit(projection));
        }

        return pgDeleteExpression;
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

    protected override Expression VisitColumn(ColumnExpression columnExpression)
    {
        if (columnExpression.TableAlias.Length > 0)
        {
            Sql.Append(columnExpression.TableAlias == "OLD"
                ? columnExpression.TableAlias
                : Dependencies.SqlGenerationHelper.DelimitIdentifier(columnExpression.TableAlias));

            Sql.Append(".");
        }

        Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columnExpression.Name));

        return columnExpression;
    }

    protected override Expression VisitTable(TableExpression tableExpression)
    {
        Sql
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(tableExpression.Name, tableExpression.Schema))
            .Append(AliasSeparator);

        Sql.Append(tableExpression.Alias == "OLD"
            ? tableExpression.Alias
            : Dependencies.SqlGenerationHelper.DelimitIdentifier(tableExpression.Alias));
        
        return tableExpression;
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

    private sealed class OuterReferenceFindingExpressionVisitor(TableExpression mainTable) : ExpressionVisitor
    {
        private bool _containsReference;

        public bool ContainsReferenceToMainTable(SqlExpression sqlExpression)
        {
            _containsReference = false;

            Visit(sqlExpression);

            return _containsReference;
        }

        [return: NotNullIfNotNull(nameof(expression))]
        public override Expression? Visit(Expression? expression)
        {
            if (_containsReference)
            {
                return expression;
            }

            if (expression is ColumnExpression { TableAlias: var tableAlias }
                && tableAlias == mainTable.Alias)
            {
                _containsReference = true;

                return expression;
            }

            return base.Visit(expression);
        }
    }

    private sealed class CloningExpressionVisitor(SqlAliasManager? sqlAliasManager, bool cloneClientProjections = true) : ExpressionVisitor
    {
        private readonly Dictionary<string, string> _tableAliasMap = [];

        [return: NotNullIfNotNull(nameof(expression))]
        public override Expression? Visit(Expression? expression)
        {
            switch (expression)
            {
                case ShapedQueryExpression shapedQuery:
                    return shapedQuery.UpdateQueryExpression(Visit(shapedQuery.QueryExpression));

                case TableExpressionBase table:
                {
                    var newTableAlias = table.Alias;
                    if (sqlAliasManager is not null && table.Alias is not null)
                    {
                        newTableAlias = sqlAliasManager.GenerateTableAlias(table.Alias);
                        _tableAliasMap[table.Alias] = newTableAlias;
                    }

                    return table is SelectExpression select
                        ? select.Clone(newTableAlias, this, cloneClientProjections)
                        : table.Clone(newTableAlias, this);
                }

                case ColumnExpression column when _tableAliasMap.TryGetValue(column.TableAlias, out var newTableAlias):
                    return new ColumnExpression(
                        column.Name, newTableAlias, column.Column, column.Type, column.TypeMapping, column.IsNullable);

                default:
                    return base.Visit(expression);
            }
        }
    }
}

internal static class SelectExtensions2
{
    private static FieldInfo sqlAliasManagerField = typeof(SelectExpression).GetField("_sqlAliasManager",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

    private static SqlAliasManager _sqlAliasManager(this SelectExpression selectExpr)
    {
        return (SqlAliasManager)sqlAliasManagerField.GetValue(selectExpr)!;
    }

    private static FieldInfo projectionMappingField = typeof(SelectExpression).GetField("_projectionMapping",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

    private static Dictionary<ProjectionMember, Expression> _projectionMapping(this SelectExpression selectExpr)
    {
        return (Dictionary<ProjectionMember, Expression>)projectionMappingField.GetValue(selectExpr)!;
    }

    private static FieldInfo clientProjectionsField = typeof(SelectExpression).GetField("_clientProjections",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

    private static List<Expression> _clientProjections(this SelectExpression selectExpr)
    {
        return (List<Expression>)clientProjectionsField.GetValue(selectExpr)!;
    }

    private static FieldInfo identifierField = typeof(SelectExpression).GetField("_identifier",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

    private static List<(ColumnExpression Column, ValueComparer Comparer)> _identifier(this SelectExpression selectExpr)
    {
        return (List<(ColumnExpression Column, ValueComparer Comparer)>)identifierField.GetValue(selectExpr)!;
    }

    private static FieldInfo childIdentifiersField = typeof(SelectExpression).GetField("_childIdentifiers",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;

    private static List<(ColumnExpression Column, ValueComparer Comparer)> _childIdentifiers(this SelectExpression selectExpr)
    {
        return (List<(ColumnExpression Column, ValueComparer Comparer)>)childIdentifiersField.GetValue(selectExpr)!;
    }

    private static PropertyInfo IsMutableProp = typeof(SelectExpression).GetProperty("IsMutable",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetProperty)!;

    private static bool IsMutable(this SelectExpression selectExpr)
    {
        return (bool)IsMutableProp.GetValue(selectExpr)!;
    }

    private static PropertyInfo AnnotationsProp = typeof(SelectExpression).GetProperty("Annotations",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetProperty)!;


    private static IReadOnlyDictionary<string, IAnnotation>? Annotations(this SelectExpression selectExpr)
    {
        return (IReadOnlyDictionary<string, IAnnotation>)AnnotationsProp.GetValue(selectExpr)!;
    }


    public static TableExpressionBase Clone(this SelectExpression selectExpr, string? alias, ExpressionVisitor cloningExpressionVisitor, bool cloneClientProjections)
    {
        var newTables = selectExpr.Tables.Select(x => (TableExpressionBase)cloningExpressionVisitor.Visit(x)).ToList();
        var tpcTablesMap = selectExpr.Tables.Select(TableExpressionExtensions.UnwrapJoin).Zip(newTables.Select(TableExpressionExtensions.UnwrapJoin))
            .Where(e => e.First is TpcTablesExpression)
            .ToDictionary(e => (TpcTablesExpression)e.First, e => (TpcTablesExpression)e.Second);

        var newProjectionMappings = new Dictionary<ProjectionMember, Expression>(selectExpr._projectionMapping().Count);
        foreach (var (projectionMember, value) in selectExpr._projectionMapping())
        {
            newProjectionMappings[projectionMember] = cloningExpressionVisitor.Visit(value);
        }

        var newClientProjections = cloneClientProjections
            ? selectExpr._clientProjections().Select(p => cloningExpressionVisitor.Visit(p)).ToList()
            : [];

        var newProjections = selectExpr.Projection.Select(x => (ProjectionExpression)cloningExpressionVisitor.Visit(x)).ToList();

        var predicate = (SqlExpression?)cloningExpressionVisitor.Visit(selectExpr.Predicate);
        var newGroupBy = selectExpr.GroupBy.Select(x => (SqlExpression)cloningExpressionVisitor.Visit(x))
            .Where(e => e is not (SqlConstantExpression or SqlParameterExpression))
            .ToList();
        var havingExpression = (SqlExpression?)cloningExpressionVisitor.Visit(selectExpr.Having);
        var newOrderings = selectExpr.Orderings.Select(x => (OrderingExpression)cloningExpressionVisitor.Visit(x)).ToList();
        var offset = (SqlExpression?)cloningExpressionVisitor.Visit(selectExpr.Offset);
        var limit = (SqlExpression?)cloningExpressionVisitor.Visit(selectExpr.Limit);

        var newSelectExpression = new SelectExpression(
            alias, newTables, predicate, newGroupBy, havingExpression, newProjections, selectExpr.IsDistinct, newOrderings, offset, limit,
            selectExpr.Tags, selectExpr.Annotations()!.ToDictionary(x => x.Key, x => x.Value), 
            selectExpr._sqlAliasManager(), selectExpr.IsMutable())
        {
            //_projectionMapping = newProjectionMappings,
            //_clientProjections = newClientProjections,
        };
        projectionMappingField.SetValue(newSelectExpression, newProjectionMappings);
        clientProjectionsField.SetValue(newSelectExpression, newClientProjections);

        foreach (var (column, comparer) in _identifier(selectExpr))
        {
            newSelectExpression._identifier().Add(((ColumnExpression)cloningExpressionVisitor.Visit(column), comparer));
        }

        foreach (var (column, comparer) in _childIdentifiers(selectExpr))
        {
            newSelectExpression._childIdentifiers().Add(((ColumnExpression)cloningExpressionVisitor.Visit(column), comparer));
        }

        return newSelectExpression;
    }
}