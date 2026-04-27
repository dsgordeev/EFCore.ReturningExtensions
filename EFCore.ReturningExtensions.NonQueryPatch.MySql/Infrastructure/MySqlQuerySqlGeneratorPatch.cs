using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure.Internal;
using Pomelo.EntityFrameworkCore.MySql.Query.ExpressionVisitors.Internal;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.NonQueryPatch.MySql.Infrastructure;

public class MySqlQuerySqlGeneratorFactoryPatch : MySqlQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;
    private readonly IMySqlOptions _options;

#if NET7_0
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public MySqlQuerySqlGeneratorFactoryPatch(QuerySqlGeneratorDependencies dependencies, IRelationalTypeMappingSource typeMappingSource) 
        : base(dependencies, typeMappingSource)
    {
        _typeMappingSource = typeMappingSource;
    }

    public override QuerySqlGenerator Create()
    {
        return new MySqlQuerySqlGeneratorPatch(Dependencies, _typeMappingSource);
    }
#else
    public MySqlQuerySqlGeneratorFactoryPatch(QuerySqlGeneratorDependencies dependencies, IMySqlOptions options)
        : base(dependencies, options)
    {
        _dependencies = dependencies;
        _options = options;
    }

    public override QuerySqlGenerator Create()
    {
        return new MySqlQuerySqlGeneratorPatch(_dependencies, _options);
    }
#endif
}

public class MySqlQuerySqlGeneratorPatch : MySqlQuerySqlGenerator, IQuerySqlGeneratorPatch
{
    private string _removeTableAliasOld;
    private string _removeTableAliasNew;

    public MySqlQuerySqlGeneratorPatch(QuerySqlGeneratorDependencies dependencies, IMySqlOptions options
#if NET7_0
            , IRelationalTypeMappingSource typeMappingSource
#endif
    )
        : base(dependencies, options
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
            && selectExpression.Having == null
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Projection.Count == 0
            && (selectExpression.Tables.Count == 1 || selectExpression.Orderings.Count == 0 && selectExpression.Limit is null))
        {
            var removeSingleTableAlias = selectExpression.Tables.Count == 1 &&
                selectExpression.Orderings.Count > 0 || selectExpression.Limit is not null;

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

                Visit(selectExpression.Predicate);
            }

            GenerateOrderings(selectExpression);
            GenerateLimitOffset(selectExpression);

            if (removeSingleTableAlias)
            {
                _removeTableAliasOld = null!;
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
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Projection.Count == 0)
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

            Sql.AppendLine().Append("SET ");
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

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");
                Visit(selectExpression.Predicate);
            }

            GenerateLimitOffset(selectExpression);

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