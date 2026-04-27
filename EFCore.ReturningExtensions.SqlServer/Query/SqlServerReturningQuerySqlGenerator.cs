using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.NonQueryPatch.Infrastructure;
using EFCore.ReturningExtensions.SqlServer.Expressions;
using EFCore.ReturningExtensions.SqlServer.Extensions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.SqlServer.Query;

internal class SqlServerReturningQuerySqlGeneratorFactory : IQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;
    //private readonly IRelationalTypeMappingSource _typeMappingSource;
    //private readonly ISqlServerSingletonOptions _sqlServerSingletonOptions;

    public SqlServerReturningQuerySqlGeneratorFactory(
        QuerySqlGeneratorDependencies dependencies//,
        //IRelationalTypeMappingSource typeMappingSource,
        //ISqlServerSingletonOptions sqlServerSingletonOptions
        )
    {
        _dependencies = dependencies;
        //_typeMappingSource = typeMappingSource;
        //_sqlServerSingletonOptions = sqlServerSingletonOptions;
    }

    public QuerySqlGenerator Create()
    {
        return new SqlServerReturningQuerySqlGenerator(_dependencies
            //, _typeMappingSource, _sqlServerSingletonOptions
            );
    }
}

internal class SqlServerReturningQuerySqlGenerator : SqlServerQuerySqlGeneratorPatch
{
    private static readonly FieldInfo? SqlField = null!;
    private IRelationalCommandBuilder? _commandBuilder;

    public SqlServerReturningQuerySqlGenerator(
        QuerySqlGeneratorDependencies dependencies//,
        //IRelationalTypeMappingSource typeMappingSource,
        //ISqlServerSingletonOptions sqlServerSingletonOptions
        )
        : base(dependencies
            //, typeMappingSource, sqlServerSingletonOptions
            )
    {
    }

    protected override IRelationalCommandBuilder Sql
        => _commandBuilder;

    public override IRelationalCommand GetCommand(SelectExpression selectExpression)
    {
        if (SqlField != null!)
        {
            SqlField.SetValue(this, _commandBuilder = Dependencies.RelationalCommandBuilderFactory.Create());
        }
        else
        {
            var type = GetType().BaseType;
            while (_commandBuilder == null! && type != null)
            {
                var fields = type!.GetFields(System.Reflection.BindingFlags.NonPublic |
                                             System.Reflection.BindingFlags.Instance);
                var sqlField = fields.FirstOrDefault(x => x.FieldType == typeof(IRelationalCommandBuilder));

                if (sqlField != null)
                {
                    sqlField.SetValue(this, _commandBuilder = Dependencies.RelationalCommandBuilderFactory.Create());

                    break;
                }

                type = type.BaseType;
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

        return _commandBuilder.Build();
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

    protected virtual void GenerateRootCommand(Expression queryExpression)
    {
        if (queryExpression.ExtractReturningExpression() is { } returningExpression)
        {
            Visit(returningExpression);

            return;
        }

        //base.GenerateRootCommand(queryExpression);

        base.Visit(queryExpression);
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
                var projection = expression.Projections[i];
                Visit(new TableAliasSelector("INSERTED", "DELETED").Visit(projection));
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
            ReturningUpdateExpressionExtensions.InternalUpdatedMethodInfo.Name);
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
                var projection = expression.Projections[i];
                Visit(new TableAliasSelector("DELETED", "DELETED").Visit(projection));
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
            ReturningDeleteExpressionExtensions.InternalDeletedMethodInfo.Name);
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

        public virtual SqlExpression ApplyTypeMapping(RelationalTypeMapping? typeMapping)
        {
            return new CustomTableAliasColumnExpression(TableAlias, _columnExpression);
        }


        public override string Name { get; }
        public override TableExpressionBase Table { get; }
        public override string TableAlias { get; }
        public override bool IsNullable { get; }
    }
    
    private class TableAliasSelector : ExpressionVisitor
    {
        private readonly string _x;
        private readonly string _y;

        public TableAliasSelector(string x, string y)
        {
            _x = x;
            _y = y;
        }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is DeletedColumnExpression deletedColumnExpression)
            {
                return new CustomTableAliasColumnExpression(
                    _y,
                    deletedColumnExpression);
            }
            else if (node is ColumnExpression columnExpression)
            {
                return new CustomTableAliasColumnExpression(
                    _x,
                    columnExpression);
            }

            return base.VisitExtension(node);
        }
    }
}