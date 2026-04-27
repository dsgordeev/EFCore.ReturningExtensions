using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Npgsql.Expressions;
using EFCore.ReturningExtensions.Npgsql.Extensions;
using Microsoft.EntityFrameworkCore;
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
                    let t = updateExpression.SelectExpression.Clone()
                    let left = new TableColumnExpression(key.Name, new TableReferenceExpression(t, tableAlias), key.ClrType, key.GetRelationalTypeMapping(), key.IsNullable) 
                    let right = new TableColumnExpression(key.Name, new TableReferenceExpression(t, "OLD"), key.ClrType, key.GetRelationalTypeMapping(), key.IsNullable) 
                    select new SqlBinaryExpression(ExpressionType.Equal, left, right, typeof(bool), NpgsqlBoolTypeMapping.Default))
                    .Aggregate<SqlBinaryExpression, SqlBinaryExpression?>(null, (current, equality) => 
                        current == null
                        ? equality
                        : new SqlBinaryExpression(ExpressionType.Equal, current, equality, typeof(bool), NpgsqlBoolTypeMapping.Default));

                var table = updateExpression.SelectExpression.Clone().Tables[0];
                var alias = table.GetType()
                    .GetProperty(nameof(table.Alias),
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty)!
                    .SetMethod!;
                alias.Invoke(table, ["OLD"]);

                tables.Add(new InnerJoinExpression(
                    table,
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

        throw new InvalidOperationException();
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

    private class CustomTableAliasColumnExpression : ColumnExpression
    {
        private readonly ColumnExpression _columnExpression;

        public CustomTableAliasColumnExpression(string tableAlias, ColumnExpression columnExpression) : base(
            columnExpression.Type,
            columnExpression.TypeMapping)
        {
            _columnExpression = columnExpression;

            Name = _columnExpression.Name;
            Table = _columnExpression.Table;
            TableAlias = tableAlias;
            IsNullable = _columnExpression.IsNullable;
        }

        public override CustomTableAliasColumnExpression MakeNullable() =>
            IsNullable
                ? this
                : new CustomTableAliasColumnExpression(TableAlias, _columnExpression.MakeNullable());

        public override SqlExpression ApplyTypeMapping(RelationalTypeMapping? typeMapping)
        {
            return new CustomTableAliasColumnExpression(TableAlias, (ColumnExpression)_columnExpression.ApplyTypeMapping(typeMapping));
        }


        public override string Name { get; }
        public override TableExpressionBase Table { get; }
        public override string TableAlias { get; }
        public override bool IsNullable { get; }
    }

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

    private sealed class TableColumnExpression(
        string name,
        TableReferenceExpression table,
        Type type,
        RelationalTypeMapping? typeMapping,
        bool nullable)
        : ColumnExpression(type, typeMapping)
    {
        private readonly TableReferenceExpression _table = table;

        public override string Name { get; } = name;

        public override TableExpressionBase Table
            => _table.Table;

        public override string TableAlias
            => _table.Alias;

        public override bool IsNullable { get; } = nullable;

        /// <inheritdoc />
        protected override Expression VisitChildren(ExpressionVisitor visitor)
        {
            // We only need to visit the table reference expression since TableReferenceUpdatingExpressionVisitor may need to modify it; it
            // mutates TableReferenceExpression (a new TableReferenceExpression is never returned).
            var newTable = (TableReferenceExpression)visitor.Visit(_table);
            //Check.DebugAssert(newTable == _table, $"New {nameof(TableReferenceExpression)} returned during visitation!");

            return this;
        }

        public override TableColumnExpression MakeNullable()
            => IsNullable ? this : new TableColumnExpression(Name, _table, Type, TypeMapping, true);

        public override SqlExpression ApplyTypeMapping(RelationalTypeMapping? typeMapping)
            => new TableColumnExpression(Name, _table, Type, typeMapping, IsNullable);

        /// <inheritdoc />
        public override bool Equals(object? obj)
            => obj != null
                && (ReferenceEquals(this, obj)
                    || obj is TableColumnExpression concreteColumnExpression
                    && Equals(concreteColumnExpression));

        private bool Equals(TableColumnExpression concreteColumnExpression)
            => base.Equals(concreteColumnExpression)
                && Name == concreteColumnExpression.Name
                && _table.Equals(concreteColumnExpression._table)
                && IsNullable == concreteColumnExpression.IsNullable;

        /// <inheritdoc />
        public override int GetHashCode()
            => HashCode.Combine(base.GetHashCode(), Name, _table, IsNullable);
    }
}