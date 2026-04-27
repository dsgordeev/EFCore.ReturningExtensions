using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.NonQueryPatch.Npgsql.Infrastructure;

public class NpgsqlQuerySqlGeneratorFactoryPatch : NpgsqlQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;
    private readonly INpgsqlSingletonOptions _npgsqlOptions;
#if NET7_0
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public NpgsqlQuerySqlGeneratorFactoryPatch(QuerySqlGeneratorDependencies dependencies, IRelationalTypeMappingSource typeMappingSource) 
        : base(dependencies, typeMappingSource)
    {
        _typeMappingSource = typeMappingSource;
    }

    public override QuerySqlGenerator Create()
    {
        return new NpgsqlQuerySqlGeneratorPatch(Dependencies, _typeMappingSource);
    }
#else
    public NpgsqlQuerySqlGeneratorFactoryPatch(
        QuerySqlGeneratorDependencies dependencies,
        INpgsqlSingletonOptions npgsqlOptions)
        : base(dependencies, npgsqlOptions)
    {
        _dependencies = dependencies;
        _npgsqlOptions = npgsqlOptions;
    }

    public override QuerySqlGenerator Create()
    {
        return new NpgsqlQuerySqlGeneratorPatch(
            _dependencies, 
            _npgsqlOptions.ReverseNullOrderingEnabled,
            _npgsqlOptions.PostgresVersion);
    }
#endif
}

public class NpgsqlQuerySqlGeneratorPatch : NpgsqlQuerySqlGenerator, IQuerySqlGeneratorPatch
{
    public NpgsqlQuerySqlGeneratorPatch(
        QuerySqlGeneratorDependencies dependencies,
        bool reverseNullOrderingEnabled,
        Version postgresVersion
#if NET7_0
            , IRelationalTypeMappingSource typeMappingSource
#endif
    )
        : base(dependencies, reverseNullOrderingEnabled, postgresVersion
#if NET7_0
            , typeMappingSource
#endif
            )
    {
    }

    public virtual IRelationalCommand GetCommand2(Expression queryExpression)
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

        GenerateRootCommand2(queryExpression);

        return Sql.Build();
    }

    /// <summary>
    ///     Generates the command for the given top-level query expression. This allows providers to intercept if an expression
    ///     requires different processing when it is at top-level.
    /// </summary>
    /// <param name="queryExpression">A query expression to print in command.</param>
    protected virtual void GenerateRootCommand2(Expression queryExpression)
    {
        switch (queryExpression)
        {
            case SelectExpression selectExpression:
                VisitSelect(selectExpression);
                break;

            case DeleteExpression deleteExpression:
                VisitDelete(deleteExpression);
                break;

            case UpdateExpression updateExpression:
                VisitUpdate(updateExpression);
                break;

            default:
                base.Visit(queryExpression);
                break;
        }
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        return extensionExpression switch
        {
            DeleteExpression deleteExpression => VisitDelete(deleteExpression),
            UpdateExpression deleteExpression => VisitUpdate(deleteExpression),
            _ => base.VisitExtension(extensionExpression),
        };
    }

    protected virtual Expression VisitDelete(DeleteExpression deleteExpression)
    {
        var pgDeleteExpression = (PgDeleteExpression)
            new NpgsqlDeleteConvertingExpressionVisitor().Process(deleteExpression);

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

        return deleteExpression;
    }

    protected virtual Expression VisitUpdate(UpdateExpression updateExpression)
    {
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

            if (selectExpression.Tables.Count > 1)
            {
                Sql.AppendLine().Append("FROM ");

                for (var i = 0; i < selectExpression.Tables.Count; i++)
                {
                    var table = selectExpression.Tables[i];
                    var joinExpression = table as JoinExpressionBase;

                    if (ReferenceEquals(updateExpression.Table, joinExpression?.Table ?? table))
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

            return updateExpression;
        }

        throw new InvalidOperationException(
            "RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(nameof(RelationalQueryableExtensions.ExecuteUpdate))");
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

    private sealed class OuterReferenceFindingExpressionVisitor : ExpressionVisitor
    {
        private readonly TableExpression _mainTable;
        private bool _containsReference;

        public OuterReferenceFindingExpressionVisitor(TableExpression mainTable)
        {
            _mainTable = mainTable;
        }

        public bool ContainsReferenceToMainTable(SqlExpression sqlExpression)
        {
            _containsReference = false;

            Visit(sqlExpression);

            return _containsReference;
        }

        [return: NotNullIfNotNull("expression")]
        public override Expression? Visit(Expression? expression)
        {
            if (_containsReference)
            {
                return expression;
            }

            if (expression is ColumnExpression columnExpression
                && columnExpression.Table == _mainTable)
            {
                _containsReference = true;

                return expression;
            }

            return base.Visit(expression);
        }
    }

    public sealed class PgDeleteExpression : Expression, IPrintableExpression
    {
        /// <summary>
        ///     The tables that rows are to be deleted from.
        /// </summary>
        public TableExpression Table { get; }

        /// <summary>
        ///     Additional tables which can be referenced in the predicate.
        /// </summary>
        public IReadOnlyList<TableExpressionBase> FromItems { get; }

        /// <summary>
        ///     The WHERE predicate for the DELETE.
        /// </summary>
        public SqlExpression? Predicate { get; }

        /// <summary>
        ///     The list of tags applied to this <see cref="DeleteExpression" />.
        /// </summary>
        public ISet<string> Tags { get; }

        /// <summary>
        ///     Creates a new instance of the <see cref="PgDeleteExpression" /> class.
        /// </summary>
        public PgDeleteExpression(
            TableExpression table,
            IReadOnlyList<TableExpressionBase> fromItems,
            SqlExpression? predicate,
            ISet<string> tags)
        {
            (Table, FromItems, Predicate, Tags) = (table, fromItems, predicate, tags);
        }

        /// <inheritdoc />
        public override Type Type
            => typeof(object);

        /// <inheritdoc />
        public override ExpressionType NodeType
            => ExpressionType.Extension;

        /// <inheritdoc />
        protected override Expression VisitChildren(ExpressionVisitor visitor)
            => Predicate is null
                ? this
                : Update((SqlExpression?)visitor.Visit(Predicate));

        /// <summary>
        ///     Creates a new expression that is like this one, but using the supplied children. If all of the children are the same, it will
        ///     return this expression.
        /// </summary>
        /// <param name="predicate">The <see cref="Predicate" /> property of the result.</param>
        public PgDeleteExpression Update(SqlExpression? predicate)
            => predicate == Predicate
                ? this
                : new PgDeleteExpression(Table, FromItems, predicate, Tags);

        /// <inheritdoc />
        public void Print(ExpressionPrinter expressionPrinter)
        {
            expressionPrinter.AppendLine($"DELETE FROM {Table.Name} AS {Table.Alias}");

            if (FromItems.Count > 0)
            {
                var first = true;
                foreach (var fromItem in FromItems)
                {
                    if (first)
                    {
                        expressionPrinter.Append("USING ");
                        first = false;
                    }
                    else
                    {
                        expressionPrinter.Append(", ");
                    }

                    expressionPrinter.Visit(fromItem);
                }
            }

            if (Predicate is not null)
            {
                expressionPrinter.Append("WHERE ");
                expressionPrinter.Visit(Predicate);
            }
        }

        /// <inheritdoc />
        public override bool Equals(object? obj)
            => obj != null
                && (ReferenceEquals(this, obj)
                    || obj is PgDeleteExpression pgDeleteExpression
                    && Equals(pgDeleteExpression));

        private bool Equals(PgDeleteExpression pgDeleteExpression)
            => Table == pgDeleteExpression.Table
                && FromItems.SequenceEqual(pgDeleteExpression.FromItems)
                && (Predicate is null ? pgDeleteExpression.Predicate is null : Predicate.Equals(pgDeleteExpression.Predicate));

        /// <inheritdoc />
        public override int GetHashCode()
            => Table.GetHashCode();
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
}