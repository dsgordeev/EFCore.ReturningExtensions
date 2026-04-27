using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.SqlServer.Expressions;
using EFCore.ReturningExtensions.SqlServer.Extensions;
using EFCore.ReturningExtensions.SqlServer.Query.Extensions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Query.Internal;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.SqlServer.Query;

internal class SqlServerReturningQueryableMethodTranslatingExpressionVisitorFactory(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
    ISqlServerSingletonOptions sqlServerSingletonOptions)
    : IQueryableMethodTranslatingExpressionVisitorFactory
{
    public QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
    {
        var ctor = typeof(SqlServerQueryableMethodTranslatingExpressionVisitor).GetConstructors();

        var sqlServerQueryCompilationContext =
            (SqlServerQueryCompilationContext)queryCompilationContext;

        var visitor = (SqlServerQueryableMethodTranslatingExpressionVisitor)ctor[0].Invoke([
            dependencies,
            relationalDependencies,
            sqlServerQueryCompilationContext,
            sqlServerSingletonOptions]);

        return new SqlServerReturningQueryableMethodTranslatingExpressionVisitor(visitor);
    }
}

public class SqlServerReturningQueryableMethodTranslatingExpressionVisitor(
    SqlServerQueryableMethodTranslatingExpressionVisitor parentVisitor)
    : SqlServerQueryableMethodTranslatingExpressionVisitor(parentVisitor)
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
                             && genericMethod == ReturningUpdateExpressionExtensions.InternalUpdatedMethodInfo
                             || x == ReturningUpdateExpressionExtensions.InternalUpdated2MethodInfo.Name
                             && genericMethod == ReturningUpdateExpressionExtensions.InternalUpdated2MethodInfo:
                        List<ProjectionExpression> projections;
                        List<Expression> projectionsClientProjections = [];
                        Dictionary<string, string> aliasMap = [];
                        {
                            var projectionsSource = Visit(methodCallExpression.Arguments[4]);

                            var selector = UnwrapLambdaFromQuote(methodCallExpression.Arguments[2]);

                            var projectionsSelect = TranslateSelect(
                                (ShapedQueryExpression)projectionsSource,
                                UnwrapLambdaFromQuote(methodCallExpression.Arguments[2]));

                            var deletedList = new List<List<bool>>();
                            var deletedOnly = false;

                            if (selector.Parameters[0] == selector.Body)
                            {
                            }
                            else if (selector.Parameters.Count > 1 && selector.Parameters[1] == selector.Body)
                            {
                                deletedOnly = true;
                            }
                            else if (selector.Body is NewExpression anonymousType)
                            {
                                foreach (var argument in anonymousType.Arguments)
                                {
                                    var list = new List<MemberExpression>();
                                    new MemberExpressionVisitor(list).Visit(argument);
                                    var deleted = list
                                        .Select(m => m.Expression != selector.Parameters[0])
                                        .ToList();

                                    deletedList.Add(deleted);
                                }
                            }
                            else throw new InvalidOperationException();

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

                            for (var i = 0; i < projections.Count; i++)
                            {
                                if (deletedList.Count > 0 && i < deletedList.Count && deletedList[i].Count > 0)
                                {
                                    var flags = deletedList[i];

                                    projections[i] = (ProjectionExpression)
                                        new ListedColumnExpressionVisitor(flags).Visit(projections[i]);
                                }
                                else if (deletedOnly)
                                {
                                    projections[i] = (ProjectionExpression)
                                        new DeletedColumnExpressionVisitor().Visit(projections[i]);
                                }
                            }

                            foreach (var table in projectionsSelectQueryExpression.Tables)
                            {
                                var patternAlias = table switch
                                {
                                    TableExpression t => t.Alias,
                                    JoinExpressionBase t => t.Table.Alias,
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
                                JoinExpressionBase t => t.Table.Alias,
                                _ => throw new InvalidOperationException(nameof(TableExpression.Alias))
                            };

                            var i = aliasMap.Keys.Skip(j++).FirstOrDefault();
                            if (i != null)
                            {
                                aliasMap[i] = targetAlias!;
                            }
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
                            .FindExtension<SqlServerDbContextOptionsBuilderExtensions.ReturningExtensionsSqlServerOptionsExtension>()!;

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
                                    JoinExpressionBase t => t.Table.Alias,
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
                                JoinExpressionBase t => t.Table.Alias,
                                _ => throw new InvalidOperationException(nameof(TableExpression.Alias))
                            };

                            var i = aliasMap.Keys.Skip(j++).FirstOrDefault();
                            if (i != null)
                            {
                                aliasMap[i] = targetAlias!;
                            }
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
                            .FindExtension<SqlServerDbContextOptionsBuilderExtensions.ReturningExtensionsSqlServerOptionsExtension>()!;

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

    protected override ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector)
    {
        if (selector.Body == selector.Parameters[0])
        {
            return base.TranslateSelect(source, selector);
        }

        if (selector.Parameters.Count > 1 && selector.Body == selector.Parameters[1])
        {
            var body = ReplacingExpressionVisitor.Replace(selector.Parameters[1], selector.Parameters[0], selector.Body);

            return base.TranslateSelect(source, Expression.Lambda(body, selector.Parameters));
        }

        var selectExpression = (SelectExpression)source.QueryExpression;
        if (selectExpression.IsDistinct)
        {
            selectExpression.PushdownIntoSubquery();
        }

        var newSelectorBody = RemapLambdaBody(source, selector);

        var sqlTranslator = RelationalDependencies.RelationalSqlTranslatingExpressionVisitorFactory.Create(QueryCompilationContext, this);
        var projectionBindingExpressionVisitor = new RelationalProjectionBindingExpressionVisitor(this, sqlTranslator);

        return source.UpdateShaperExpression(projectionBindingExpressionVisitor.Translate(selectExpression, newSelectorBody));
    }

    private Expression RemapLambdaBody(ShapedQueryExpression shapedQueryExpression, LambdaExpression lambdaExpression)
    {
        var lambdaBody = ReplacingExpressionVisitor.Replace(
            lambdaExpression.Parameters.First(), shapedQueryExpression.ShaperExpression, lambdaExpression.Body);

        if (lambdaExpression.Parameters.Count > 1)
        {
            lambdaBody = ReplacingExpressionVisitor.Replace(
                lambdaExpression.Parameters.Skip(1).First(), shapedQueryExpression.ShaperExpression, lambdaBody);
        }

        MethodInfo? expandSharedTypeEntities = null;

        for (var t = GetType().BaseType!; t != typeof(object); t = t.BaseType!)
        {
            expandSharedTypeEntities = t.GetMethod("ExpandSharedTypeEntities",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (expandSharedTypeEntities is not null)
            {
                break;
            }
        }

        expandSharedTypeEntities = expandSharedTypeEntities ?? throw new InvalidOperationException();

        return (Expression)expandSharedTypeEntities.Invoke(this, [(SelectExpression)shapedQueryExpression.QueryExpression, lambdaBody])!;
    }

    private static LambdaExpression UnwrapLambdaFromQuote(Expression expression)
        => (LambdaExpression)(expression is UnaryExpression unary && expression.NodeType == ExpressionType.Quote
            ? unary.Operand
            : expression);

    private class MemberExpressionVisitor(List<MemberExpression> list) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            list.Add(node);

            return base.VisitMember(node);
        }
    }

    private class ListedColumnExpressionVisitor(List<bool> list) : ExpressionVisitor
    {
        private readonly List<ColumnExpression> _list = [];

        protected override Expression VisitExtension(Expression node)
        {
            if (node is ColumnExpression column)
            {
                _list.Add(column);

                if (list[_list.Count - 1])
                {
                    return new DeletedColumnExpression(column.TableAlias, column);
                }

                return column;
            }

            return base.VisitExtension(node);
        }
    }

    private class DeletedColumnExpressionVisitor : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is ColumnExpression column)
            {
                return new DeletedColumnExpression(column.TableAlias, column);
            }

            return base.VisitExtension(node);
        }
    }

    private class ColumnAliasReplaceExpressionVisitor(Dictionary<string, string> aliasMap) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is DeletedColumnExpression deletedColumn)
            {
                if (aliasMap.TryGetValue(deletedColumn.TableAlias, out var alias) && !string.IsNullOrWhiteSpace(alias))
                {
                    return new DeletedColumnExpression(
                        alias,
                        deletedColumn);
                }
            }
            else if (node is ColumnExpression column)
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
}