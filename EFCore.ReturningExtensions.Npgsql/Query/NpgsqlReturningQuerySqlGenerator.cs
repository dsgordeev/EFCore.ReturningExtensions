using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using EFCore.ReturningExtensions.NonQueryPatch.Npgsql.Infrastructure;
using EFCore.ReturningExtensions.Npgsql.Expressions;
using EFCore.ReturningExtensions.Npgsql.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
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
    INpgsqlSingletonOptions npgsqlOptions,
    IModel model)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
    {
        return new NpgsqlReturningQuerySqlGenerator(
            dependencies,
            npgsqlOptions.ReverseNullOrderingEnabled,
            npgsqlOptions.PostgresVersion,
            model);
    }
}

internal class NpgsqlReturningQuerySqlGenerator(
    QuerySqlGeneratorDependencies dependencies,
    bool reverseNullOrderingEnabled,
    Version postgresVersion,
    IModel model)
    : NpgsqlQuerySqlGeneratorPatch(
        dependencies,
        reverseNullOrderingEnabled,
        postgresVersion)
{
    private readonly Version _postgresVersion = postgresVersion;
    private readonly IModel _model = model;

    public override IRelationalCommand GetCommand(SelectExpression selectExpression)
    {
        // Sql = SqlFactory.Create();

        for (var type = GetType().BaseType; type != null; type = type.BaseType)
        {
            var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            var sqlField = fields.FirstOrDefault(x => x.FieldType == typeof(IRelationalCommandBuilder));
            if (sqlField != null && sqlField.GetValue(this) == null)
            {
                sqlField.SetValue(this, Dependencies.RelationalCommandBuilderFactory.Create());

                break;
            }
        }

        if (selectExpression.IsNonComposedFromSql())
        {
            GenerateTagsHeaderComment(selectExpression);

            GenerateFromSql((FromSqlExpression)selectExpression.Tables[0]);
        }
        else
        {
            VisitSelect(selectExpression);
        }

        return Sql.Build();
    }

    private void GenerateFromSql(FromSqlExpression fromSqlExpression)
    {
        var sql = fromSqlExpression.Sql;
        string[]? substitutions = null;

        switch (fromSqlExpression.Arguments)
        {
            case ConstantExpression { Value: CompositeRelationalParameter compositeRelationalParameter }:
                {
                    var subParameters = compositeRelationalParameter.RelationalParameters;
                    substitutions = new string[subParameters.Count];
                    for (var i = 0; i < subParameters.Count; i++)
                    {
                        substitutions[i] = Dependencies.SqlGenerationHelper.GenerateParameterNamePlaceholder(subParameters[i].InvariantName);
                    }

                    Sql.AddParameter(compositeRelationalParameter);

                    break;
                }

            case ConstantExpression { Value: object[] constantValues }:
                {
                    substitutions = new string[constantValues.Length];
                    for (var i = 0; i < constantValues.Length; i++)
                    {
                        var value = constantValues[i];
                        if (value is RawRelationalParameter rawRelationalParameter)
                        {
                            substitutions[i] = Dependencies.SqlGenerationHelper.GenerateParameterNamePlaceholder(rawRelationalParameter.InvariantName);
                            Sql.AddParameter(rawRelationalParameter);
                        }
                        else if (value is SqlConstantExpression sqlConstantExpression)
                        {
                            substitutions[i] = sqlConstantExpression.TypeMapping!.GenerateSqlLiteral(sqlConstantExpression.Value);
                        }
                    }

                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(fromSqlExpression),
                    fromSqlExpression.Arguments,
                    RelationalStrings.InvalidFromSqlArguments(
                        fromSqlExpression.Arguments.GetType(),
                        fromSqlExpression.Arguments is ConstantExpression constantExpression
                            ? constantExpression.Value?.GetType()
                            : null));
        }

        // ReSharper disable once CoVariantArrayConversion
        // InvariantCulture not needed since substitutions are all strings
        sql = string.Format(sql, substitutions);

        Sql.AppendLines(sql);
    }

    protected override Expression VisitSelect(SelectExpression selectExpression)
    {
        if (selectExpression.ExtractReturningExpression() is { } returningExpression)
        {
            GenerateTagsHeaderComment(selectExpression);

            return Visit(returningExpression);
        }

        GenerateTagsHeaderComment(selectExpression);

        return base.VisitSelect(selectExpression);
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
                    .Select(m => m.EntityType)
                    .FirstOrDefault()!.ClrType;

                var model = updateExpression.Table.Table.Model.Model;
                var keys = model.FindEntityType(entityType)!.FindPrimaryKey()!.Properties;

                static TableExpression BuildTableExpression(SelectExpression table, string alias)
                {
                    var ctor = typeof(TableExpression).GetConstructor(BindingFlags.NonPublic | BindingFlags.CreateInstance | BindingFlags.Instance, [typeof(ITableBase)])!;
                    var te = (TableExpression)ctor.Invoke([(ITableBase)((TableExpression)table.Tables[0]).Table]);
                    var p = typeof(TableExpression).GetProperty(nameof(TableExpression.Alias), BindingFlags.Public | BindingFlags.GetProperty | BindingFlags.Instance)!;
                    p.SetValue(te, alias);
                    return te;
                }

                var joinPredicate = (
                    from key in keys
                    let t = updateExpression.SelectExpression.Clone()
                    let left = new TableColumnExpression(key.Name, BuildTableExpression(t, tableAlias), key.ClrType, key.GetRelationalTypeMapping(), key.IsNullable) 
                    let right = new TableColumnExpression(key.Name, BuildTableExpression(t, "OLD"), key.ClrType, key.GetRelationalTypeMapping(), key.IsNullable) 
                    select new SqlBinaryExpression(ExpressionType.Equal, left, right, typeof(bool), NpgsqlBoolTypeMapping.NullMapping))
                    .Aggregate<SqlBinaryExpression, SqlBinaryExpression?>(null, (current, equality) => 
                        current == null
                        ? equality
                        : new SqlBinaryExpression(ExpressionType.Equal, current, equality, typeof(bool), NpgsqlBoolTypeMapping.NullMapping));

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
        TableExpression table,
        Type type,
        RelationalTypeMapping? typeMapping,
        bool nullable)
        : ColumnExpression(type, typeMapping)
    {
        private readonly TableExpression _table = table;

        public override string Name { get; } = name;

        public override TableExpressionBase Table
            => _table;

        public override string TableAlias
            => _table.Alias;

        public override bool IsNullable { get; } = nullable;

        /// <inheritdoc />
        protected override Expression VisitChildren(ExpressionVisitor visitor)
        {
            // We only need to visit the table reference expression since TableReferenceUpdatingExpressionVisitor may need to modify it; it
            // mutates TableReferenceExpression (a new TableReferenceExpression is never returned).
            var newTable = (TableExpression)visitor.Visit(_table);
            //Check.DebugAssert(newTable == _table, $"New {nameof(TableReferenceExpression)} returned during visitation!");

            return this;
        }

        public override TableColumnExpression MakeNullable()
            => IsNullable ? this : new TableColumnExpression(Name, _table, Type, TypeMapping, true);

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

public class NpgsqlDeleteConvertingExpressionVisitor : ExpressionVisitor
{
    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    public virtual Expression Process(Expression node)
        => node switch
        {
            DeleteExpression deleteExpression => VisitDelete(deleteExpression),

            _ => node
        };

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    protected virtual Expression VisitDelete(DeleteExpression deleteExpression)
    {
        var selectExpression = deleteExpression.SelectExpression;

        if (selectExpression.Offset != null
            || selectExpression.Limit != null
            || selectExpression.Having != null
            || selectExpression.Orderings.Count > 0
            || selectExpression.GroupBy.Count > 0
            || selectExpression.Projection.Count > 0)
        {
            throw new InvalidOperationException(
                @"RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(
                    nameof(RelationalQueryableExtensions.ExecuteDelete))");
        }

        var fromItems = new List<TableExpressionBase>();
        SqlExpression? joinPredicates = null;

        // The SelectExpression also contains the target table being modified (same as deleteExpression.Table).
        // If it has additional inner joins, use the PostgreSQL-specific USING syntax to express the join.
        // Note that the non-join TableExpression isn't necessary the target table - through projection the last table being
        // joined may be the one being modified.
        foreach (var tableBase in selectExpression.Tables)
        {
            switch (tableBase)
            {
                case TableExpression tableExpression:
                    if (tableExpression != deleteExpression.Table)
                    {
                        fromItems.Add(tableExpression);
                    }

                    break;

                case InnerJoinExpression { Table: { } tableExpression } innerJoinExpression:
                    if (tableExpression != deleteExpression.Table)
                    {
                        fromItems.Add(tableExpression);
                    }

                    joinPredicates = joinPredicates is null
                        ? innerJoinExpression.JoinPredicate
                        : new SqlBinaryExpression(
                            ExpressionType.AndAlso, joinPredicates, innerJoinExpression.JoinPredicate, typeof(bool),
                            innerJoinExpression.JoinPredicate.TypeMapping);
                    break;

                default:
                    throw new InvalidOperationException(
                        @"RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(
                            nameof(RelationalQueryableExtensions.ExecuteDelete))");
            }
        }

        // Combine the join predicates (if any) before the user-provided predicate
        var predicate = (joinPredicates, selectExpression.Predicate) switch
        {
            (null, not null) => selectExpression.Predicate,
            (not null, null) => joinPredicates,
            (null, null) => null,
            (not null, not null) => new SqlBinaryExpression(
                ExpressionType.AndAlso, joinPredicates, selectExpression.Predicate, typeof(bool), joinPredicates.TypeMapping)
        };

        return new PgDeleteExpression(deleteExpression.Table, fromItems, predicate, deleteExpression.Tags);
    }
}