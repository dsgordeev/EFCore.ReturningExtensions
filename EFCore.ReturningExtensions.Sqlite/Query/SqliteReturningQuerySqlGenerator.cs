using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using EFCore.ReturningExtensions.NonQueryPatch.Sqlite.Infrastructure;
using EFCore.ReturningExtensions.Sqlite.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.ReturningExtensions.Sqlite.Query;

internal class SqliteReturningQuerySqlGeneratorFactory(
    QuerySqlGeneratorDependencies dependencies,
    IModel model)
    : IQuerySqlGeneratorFactory
{
    public QuerySqlGenerator Create()
    {
        return new SqliteReturningQuerySqlGenerator(dependencies, model);
    }
}

internal class SqliteReturningQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies, IModel model)
    : SqliteQuerySqlGeneratorPatch(dependencies)
{
    private readonly IModel _model = model;

    public override IRelationalCommand GetCommand(SelectExpression selectExpression)
    {
        // _relationalCommandBuilder = _relationalCommandBuilderFactory.Create();

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

    
    protected virtual Expression VisitReturningUpdate(ReturningUpdateExpression expression)
    {
        var updateExpression = expression.UpdateExpression;
        
        var selectExpression = updateExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Limit == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            // && selectExpression.Projection.Count == 0
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

            Sql.AppendLine().Append("RETURNING ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                Visit(new TableAliasCleaner().Visit(expression.Projections[i].Expression));
            }

            return updateExpression;
        }

        throw new InvalidOperationException(
            "RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(nameof(RelationalQueryableExtensions.ExecuteUpdate))");
    }

    protected virtual Expression VisitReturningDelete(ReturningDeleteExpression expression)
    {
        var deleteExpression = expression.DeleteExpression;

        var selectExpression = deleteExpression.SelectExpression;

        if (selectExpression.Offset == null
            && selectExpression.Limit == null
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Tables.Count >= 1
            && selectExpression.Tables[0] == deleteExpression.Table
            //&& selectExpression.Projection.Count == 0
            )
        {
            Sql.Append("DELETE FROM ");
            Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(deleteExpression.Table.Name, deleteExpression.Table.Schema));
            Sql.Append(AliasSeparator);
            Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(deleteExpression.Table.Alias + "z"));
            Sql.AppendLine();
            Sql.Append("WHERE ");
            Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(deleteExpression.Table.Alias + "z"));
            Sql.Append(".");
            var t = _model.FindEntityType(expression.Type.GetGenericArguments()[0])!;
            Sql.Append($"{Dependencies.SqlGenerationHelper.DelimitIdentifier(t.GetKeys().First().Properties[0].Name)} ");
            Sql.AppendLine("IN (");
            var clone = deleteExpression.SelectExpression;
            var f = typeof(SelectExpression).GetField("_projection", BindingFlags.NonPublic | BindingFlags.Instance);
            f.SetValue(clone, new List<ProjectionExpression>([(ProjectionExpression)typeof(ProjectionExpression).GetConstructor(BindingFlags.NonPublic | BindingFlags.CreateInstance, [typeof(SqlExpression), typeof(string)])!.Invoke([Dependencies.SqlGenerationHelper, deleteExpression.Table.Alias])]));
            Visit(clone);
            Sql.AppendLine(")");

            Sql.AppendLine().Append("RETURNING ");
            for (var i = 0; i < expression.Projections.Count; i++)
            {
                if (i > 0) Sql.Append(", ");
                var projection = expression.Projections[i];
                Visit(new TableAliasCleaner().Visit(projection));
            }

            return deleteExpression;
        }

        throw new InvalidOperationException(
            "RelationalStrings.ExecuteOperationWithUnsupportedOperatorInSqlGeneration(nameof(RelationalQueryableExtensions.ExecuteDelete))");
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

    private class AliasFreeColumnExpression : ColumnExpression
    {
        private readonly ColumnExpression _columnExpression;

        public AliasFreeColumnExpression(ColumnExpression columnExpression) : base(
            columnExpression.Type,
            columnExpression.TypeMapping)
        {
            _columnExpression = columnExpression;

            Name = _columnExpression.Name;
            Table = _columnExpression.Table;
            TableAlias = string.Empty;
            IsNullable = _columnExpression.IsNullable;
        }

        public override AliasFreeColumnExpression MakeNullable() =>
            IsNullable
                ? this
                : new AliasFreeColumnExpression(_columnExpression.MakeNullable());

        //public /*override*/ SqlExpression ApplyTypeMapping(RelationalTypeMapping? typeMapping)
        //{
        //    return new AliasFreeColumnExpression((ColumnExpression)_columnExpression.ApplyTypeMapping(typeMapping));
        //}

        public override string Name { get; }
        public override TableExpressionBase Table { get; }
        public override string TableAlias { get; }
        public override bool IsNullable { get; }
    }
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