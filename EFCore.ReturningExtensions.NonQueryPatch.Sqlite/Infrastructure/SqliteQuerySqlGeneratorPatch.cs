using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.NonQueryPatch.Sqlite.Infrastructure;

public class SqliteQuerySqlGeneratorFactoryPatch : SqliteQuerySqlGeneratorFactory
{
#if NET7_0
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public SqliteQuerySqlGeneratorFactoryPatch(QuerySqlGeneratorDependencies dependencies, IRelationalTypeMappingSource typeMappingSource) 
        : base(dependencies, typeMappingSource)
    {
        _typeMappingSource = typeMappingSource;
    }

    public override QuerySqlGenerator Create()
    {
        return new SqliteQuerySqlGeneratorPatch(Dependencies, _typeMappingSource);
    }
#else
    public SqliteQuerySqlGeneratorFactoryPatch(QuerySqlGeneratorDependencies dependencies)
        : base(dependencies)
    {
    }

    public override QuerySqlGenerator Create()
    {
        return new SqliteQuerySqlGeneratorPatch(Dependencies);
    }
#endif
}

public class SqliteQuerySqlGeneratorPatch : SqliteQuerySqlGenerator, IQuerySqlGeneratorPatch
{
    public SqliteQuerySqlGeneratorPatch(QuerySqlGeneratorDependencies dependencies
#if NET7_0
            , IRelationalTypeMappingSource typeMappingSource
#endif
    )
        : base(dependencies
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
        var selectExpression = deleteExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Limit == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Tables.Count == 1
            && selectExpression.Tables[0] == deleteExpression.Table
            && selectExpression.Projection.Count == 0)
        {
            Sql.Append("DELETE FROM ");
            Visit(deleteExpression.Table);

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");
                Visit(selectExpression.Predicate);
            }

            return deleteExpression;
        }

        throw new InvalidOperationException(
            "RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(nameof(RelationalQueryableExtensions.ExecuteDelete))");
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
            var firstTablePrinted = false;
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
}