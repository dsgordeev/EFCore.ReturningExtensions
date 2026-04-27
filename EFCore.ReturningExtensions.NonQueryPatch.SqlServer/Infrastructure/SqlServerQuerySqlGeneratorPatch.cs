using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.SqlServer.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.NonQueryPatch.SqlServer.Infrastructure;

public class SqlServerQuerySqlGeneratorFactoryPatch : SqlServerQuerySqlGeneratorFactory
{
#if NET7_0
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    public SqlServerQuerySqlGeneratorFactoryPatch(QuerySqlGeneratorDependencies dependencies, IRelationalTypeMappingSource typeMappingSource) 
        : base(dependencies, typeMappingSource)
    {
        _typeMappingSource = typeMappingSource;
    }

    public override QuerySqlGenerator Create()
    {
        return new SqlServerQuerySqlGeneratorPatch(Dependencies, _typeMappingSource);
    }
#else
    public SqlServerQuerySqlGeneratorFactoryPatch(QuerySqlGeneratorDependencies dependencies)
        : base(dependencies)
    {
    }

    public override QuerySqlGenerator Create()
    {
        return new SqlServerQuerySqlGeneratorPatch(Dependencies);
    }
#endif
}

public class SqlServerQuerySqlGeneratorPatch : SqlServerQuerySqlGenerator, IQuerySqlGeneratorPatch
{
    public SqlServerQuerySqlGeneratorPatch(QuerySqlGeneratorDependencies dependencies
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
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Projection.Count == 0)
        {
            Sql.Append("DELETE ");
            GenerateTop(selectExpression);

            Sql.AppendLine($"FROM {Dependencies.SqlGenerationHelper.DelimitIdentifier(deleteExpression.Table.Alias)}");

            Sql.Append("FROM ");
            GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE 1=1 AND ");

                Visit(selectExpression.Predicate);
            }

            GenerateLimitOffset(selectExpression);

            return deleteExpression;
        }

        throw new InvalidOperationException();
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

            Sql.AppendLine().Append("FROM ");
            GenerateList(selectExpression.Tables, e => Visit(e), sql => sql.AppendLine());

            if (selectExpression.Predicate != null)
            {
                Sql.AppendLine().Append("WHERE ");
                Visit(selectExpression.Predicate);
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