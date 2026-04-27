using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Sqlite.Extensions;
using EFCore.ReturningExtensions.Sqlite.Query.Extensions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.Sqlite.Query;

internal class SqliteReturningQueryMethodTranslatingExpressionVisitorFactory(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies)
    : IQueryableMethodTranslatingExpressionVisitorFactory
{
    public virtual QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
    {
        return new SqliteReturningQueryMethodTranslatingExpressionVisitor(
            dependencies,
            relationalDependencies,
            queryCompilationContext);
    }
}

internal class SqliteReturningQueryMethodTranslatingExpressionVisitor(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
    QueryCompilationContext queryCompilationContext) :
    SqliteQueryableMethodTranslatingExpressionVisitor(
        dependencies,
        relationalDependencies,
        queryCompilationContext)
{
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(ReturningUpdateExpressionExtensions))
        {
            var source = Visit(methodCallExpression.Arguments[0]);
            if (source is ShapedQueryExpression shapedQueryExpression)
            {
                var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
                switch (method.Name)
                {
                    case var x
                        when x == ReturningUpdateExpressionExtensions.InternalUpdatedMethodInfo.Name
                        && genericMethod == ReturningUpdateExpressionExtensions.InternalUpdatedMethodInfo:
                        List<ProjectionExpression> projections;
                        List<Expression> projectionsClientProjections = [];
                        Dictionary<string, string> aliasMap = [];
                        {
                            var projectionsSource = Visit(methodCallExpression.Arguments[4]);

                            var projectionsSelect = TranslateSelect(
                                (ShapedQueryExpression)projectionsSource,
                                UnwrapLambdaFromQuote(methodCallExpression.Arguments[2]));

                            var projectionsSelectQueryExpression = (SelectExpression)projectionsSelect.QueryExpression;

                            projectionsSelectQueryExpression
                                .ApplyProjection(
                                    projectionsSelect.ShaperExpression,
                                    projectionsSelect.ResultCardinality,
                                    ((RelationalQueryCompilationContext)QueryCompilationContext)
                                    .QuerySplittingBehavior.GetValueOrDefault());

                            projections = [.. projectionsSelectQueryExpression.Projection];

                            var clientProjectionField = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                            var clientProjectionValue = (List<Expression>)clientProjectionField.GetValue(projectionsSelect.QueryExpression)!;
                            projectionsClientProjections.AddRange(clientProjectionValue);

                            foreach (var table in projectionsSelectQueryExpression.Tables)
                            {
                                var patternAlias = table switch
                                {
                                    TableExpression t => t.Alias,
                                    PredicateJoinExpressionBase t => t.Table.Alias,
                                    _ => throw new InvalidOperationException(nameof(TableExpression.Alias))
                                };

                                aliasMap.Add(patternAlias!, null!);
                            }
                        }

                        var setPropertyCalls = UnwrapLambdaFromQuote(methodCallExpression.Arguments[1]);
                        var nonQuery = TranslateExecuteUpdate(shapedQueryExpression, setPropertyCalls);
                        var update = (UpdateExpression)nonQuery!.Expression;

                        var j = 0;
                        foreach (var table in update.SelectExpression.Tables)
                        {
                            var targetAlias = table switch
                            {
                                TableExpression t => t.Alias,
                                PredicateJoinExpressionBase t => t.Table.Alias,
                                _ => throw new InvalidOperationException(nameof(TableExpression.Alias))
                            };

                            aliasMap[aliasMap.Keys.Skip(j++).First()] = targetAlias!;
                        }

                        for (var i = 0; i < projections.Count; i++)
                        {
                            projections[i] = (ProjectionExpression)new ColumnAliasReplaceExpressionVisitor(aliasMap).Visit(projections[i]);
                        }

                        var projectionField = typeof(SelectExpression).GetField("_projection", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                        var projectionFieldValue = (List<ProjectionExpression>)projectionField.GetValue(update.SelectExpression)!;
                        if (projectionFieldValue.Count == 0)
                        {
                            projectionFieldValue.AddRange(projections);
                        }

                        var clientProjectionField2 = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                        var clientProjectionFieldValue = (List<Expression>)clientProjectionField2.GetValue(update.SelectExpression)!;
                        if (clientProjectionFieldValue.Count == 0)
                        {
                            clientProjectionFieldValue.AddRange(projectionsClientProjections);
                        }

                        var returningUpdateExpression = new ReturningUpdateExpression(
                            update,
                            projections,
                            shapedQueryExpression.Type);

                        var stubSelectSource = Visit(methodCallExpression.Arguments[3]);

                        var stubSelect = TranslateSelect(
                            (ShapedQueryExpression)stubSelectSource,
                            UnwrapLambdaFromQuote(methodCallExpression.Arguments[2]));

                        var projectionMappingField = typeof(SelectExpression).GetField("_projectionMapping", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                        var sourceValue = ((Dictionary<ProjectionMember, Expression>)projectionMappingField.GetValue(stubSelect.QueryExpression)!)
                            .ToDictionary(y => y.Key, y => y.Value);

                        var targetValue = (Dictionary<ProjectionMember, Expression>)projectionMappingField.GetValue(returningUpdateExpression.UpdateExpression.SelectExpression)!;
                        if (targetValue.Count == 0)
                        {
                            foreach (var (k, v) in sourceValue)
                            {
                                targetValue.Add(k, v);
                            }
                        }

                        var info = QueryCompilationContext.ContextOptions
                            .FindExtension<SqliteDbContextOptionsBuilderExtensions.ReturningExtensionsSqliteOptionsExtension>()!;

                        if (info.UseInject)
                        {
                            var enrichedSelectExpression = ((SelectExpression)stubSelect.QueryExpression)
                                .AppendExpressionAnnotation(returningUpdateExpression);

                            return stubSelect.UpdateQueryExpression(enrichedSelectExpression);
                        }

                        return stubSelect.UpdateQueryExpression(returningUpdateExpression);
                }
            }
        }
        if (method.DeclaringType == typeof(ReturningDeleteExpressionExtensions))
        {
            var source = Visit(methodCallExpression.Arguments[0]);
            if (source is ShapedQueryExpression shapedQueryExpression)
            {
                var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
                switch (method.Name)
                {
                    case var x
                        when x == ReturningDeleteExpressionExtensions.InternalDeletedMethodInfo.Name
                        && genericMethod == ReturningDeleteExpressionExtensions.InternalDeletedMethodInfo:
                        List<ProjectionExpression> projections;
                        List<Expression> projectionsClientProjections = [];
                        Dictionary<string, string> aliasMap = [];
                        {
                            var projectionsSource = Visit(methodCallExpression.Arguments[3]);

                            var projectionsSelect = TranslateSelect(
                                (ShapedQueryExpression)projectionsSource,
                                UnwrapLambdaFromQuote(methodCallExpression.Arguments[1]));

                            var projectionsSelectQueryExpression = (SelectExpression)projectionsSelect.QueryExpression;

                            projectionsSelectQueryExpression
                                .ApplyProjection(
                                    projectionsSelect.ShaperExpression,
                                    projectionsSelect.ResultCardinality,
                                    ((RelationalQueryCompilationContext)QueryCompilationContext)
                                    .QuerySplittingBehavior.GetValueOrDefault());

                            projections = [.. projectionsSelectQueryExpression.Projection];

                            var clientProjectionField = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                            var clientProjectionValue = (List<Expression>)clientProjectionField.GetValue(projectionsSelect.QueryExpression)!;
                            projectionsClientProjections.AddRange(clientProjectionValue);

                            foreach (var table in ((SelectExpression)projectionsSelect.QueryExpression).Tables)
                            {
                                var patternAlias = table switch
                                {
                                    TableExpression t => t.Alias,
                                    PredicateJoinExpressionBase t => t.Table.Alias,
                                    _ => throw new InvalidOperationException(nameof(TableExpression.Alias))
                                };

                                aliasMap.Add(patternAlias!, null!);
                            }
                        }

                        var clientProjectionField3 = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                        var clientProjectionValue3 = (List<Expression>)clientProjectionField3.GetValue(shapedQueryExpression.QueryExpression)!;
                        if (clientProjectionValue3.Count == 0)
                        {
                            clientProjectionValue3.AddRange(projectionsClientProjections);
                        }

                        var nonQuery = TranslateExecuteDelete(shapedQueryExpression);
                        var delete = (DeleteExpression)nonQuery!.Expression;

                        var j = 0;
                        foreach (var table in delete.SelectExpression.Tables)
                        {
                            var targetAlias = table switch
                            {
                                TableExpression t => t.Alias,
                                PredicateJoinExpressionBase t => t.Table.Alias,
                                _ => throw new InvalidOperationException(nameof(TableExpression.Alias))
                            };

                            aliasMap[aliasMap.Keys.Skip(j++).First()] = targetAlias!;
                        }

                        for (var i = 0; i < projections.Count; i++)
                        {
                            projections[i] = (ProjectionExpression)new ColumnAliasReplaceExpressionVisitor(aliasMap).Visit(projections[i]);
                        }

                        var projectionField = typeof(SelectExpression).GetField("_projection", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                        var projectionFieldValue = (List<ProjectionExpression>)projectionField.GetValue(delete.SelectExpression)!;
                        if (projectionFieldValue.Count == 0)
                        {
                            projectionFieldValue.AddRange(projections);
                        }

                        var clientProjectionField2 = typeof(SelectExpression).GetField("_clientProjections", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                        var clientProjectionFieldValue = (List<Expression>)clientProjectionField2.GetValue(delete.SelectExpression)!;
                        if (clientProjectionFieldValue.Count == 0)
                        {
                            clientProjectionFieldValue.AddRange(projectionsClientProjections);
                        }

                        var returningDeleteExpression = new ReturningDeleteExpression(
                            delete,
                            projections,
                            shapedQueryExpression.Type);

                        var stubSelectSource = Visit(methodCallExpression.Arguments[2]);

                        var stubSelect = TranslateSelect(
                            (ShapedQueryExpression)stubSelectSource,
                            UnwrapLambdaFromQuote(methodCallExpression.Arguments[1]));

                        var projectionMappingField = typeof(SelectExpression).GetField("_projectionMapping", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!;
                        var sourceValue = ((Dictionary<ProjectionMember, Expression>)projectionMappingField.GetValue(stubSelect.QueryExpression)!)
                            .ToDictionary(y => y.Key, y => y.Value);

                        var targetValue = (Dictionary<ProjectionMember, Expression>)projectionMappingField.GetValue(returningDeleteExpression.DeleteExpression.SelectExpression)!;
                        if (targetValue.Count == 0)
                        {
                            foreach (var (k, v) in sourceValue)
                            {
                                targetValue.Add(k, v);
                            }
                        }

                        var info = QueryCompilationContext.ContextOptions
                            .FindExtension<SqliteDbContextOptionsBuilderExtensions.ReturningExtensionsSqliteOptionsExtension>()!;

                        if (info.UseInject)
                        {
                            var enrichedSelectExpression = ((SelectExpression)stubSelect.QueryExpression)
                                .AppendExpressionAnnotation(returningDeleteExpression);

                            return stubSelect.UpdateQueryExpression(enrichedSelectExpression);
                        }

                        return stubSelect.UpdateQueryExpression(returningDeleteExpression);
                }
            }
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    private static LambdaExpression UnwrapLambdaFromQuote(Expression expression)
        => (LambdaExpression)(expression is UnaryExpression unary && expression.NodeType == ExpressionType.Quote
            ? unary.Operand
            : expression);

    private class ColumnAliasReplaceExpressionVisitor(Dictionary<string, string> aliasMap) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is ColumnExpression column)
            {
                if (aliasMap.TryGetValue(column.TableAlias, out var alias) && !string.IsNullOrWhiteSpace(alias))
                {
                    return new CustomTableAliasColumnExpression(
                        alias,
                        column);
                }
            }

            return base.VisitExtension(node);
        }
    }

    internal class CustomTableAliasColumnExpression : ColumnExpression
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
}