using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Npgsql.Expressions;
using EFCore.ReturningExtensions.Npgsql.Extensions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;
using System.Linq.Expressions;
using System.Reflection;

namespace EFCore.ReturningExtensions.Npgsql.Query;

internal class NpgsqlReturningQueryMethodTranslatingExpressionVisitorFactory(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies)
    : IQueryableMethodTranslatingExpressionVisitorFactory
{
    public QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
    {
        return new NpgsqlReturningQueryMethodTranslatingExpressionVisitor(
            dependencies,
            relationalDependencies,
            (NpgsqlQueryCompilationContext)queryCompilationContext);
    }
}

internal class NpgsqlReturningQueryMethodTranslatingExpressionVisitor(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
    NpgsqlQueryCompilationContext queryCompilationContext)
    : NpgsqlQueryableMethodTranslatingExpressionVisitor(
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
                             && genericMethod == ReturningUpdateExpressionExtensions.InternalUpdatedMethodInfo
                             || x == ReturningUpdateExpressionExtensions.InternalUpdated2MethodInfo.Name
                             && genericMethod == ReturningUpdateExpressionExtensions.InternalUpdated2MethodInfo:
                        {
                            List<ProjectionExpression> projections;
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

                                ((SelectExpression)projectionsSelect.QueryExpression)
                                    .ApplyProjection(
                                        projectionsSelect.ShaperExpression,
                                        projectionsSelect.ResultCardinality,
                                        ((RelationalQueryCompilationContext)QueryCompilationContext)
                                        .QuerySplittingBehavior.GetValueOrDefault());

                                projections = ((SelectExpression)projectionsSelect.QueryExpression).Projection.ToList();

                                for (var i = 0; i < projections.Count; i++)
                                {
                                    if (deletedList.Count > 0)
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
                            }


                            var setPropertyCalls = UnwrapLambdaFromQuote(methodCallExpression.Arguments[1]);
                            var nonQuery = TranslateExecuteUpdate(shapedQueryExpression, setPropertyCalls);
                            var update = (UpdateExpression)nonQuery!.Expression;

                            new ReturningUpdateExpression(
                                    update,
                                    projections)
                                .SaveInto(QueryCompilationContext);
                        }

                        var stubSelectSource = Visit(methodCallExpression.Arguments[3]);

                        var stubSelect = TranslateSelect(
                            (ShapedQueryExpression)stubSelectSource,
                            UnwrapLambdaFromQuote(methodCallExpression.Arguments[2]));

                        return stubSelect;
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
                        {
                            IReadOnlyList<ProjectionExpression> projections;
                            {
                                var projectionsSource = Visit(methodCallExpression.Arguments[3]);

                                var projectionsSelect = TranslateSelect(
                                    (ShapedQueryExpression)projectionsSource,
                                    UnwrapLambdaFromQuote(methodCallExpression.Arguments[1]));

                                new SelectExpressionProjectionApplyingExpressionVisitor(((RelationalQueryCompilationContext)QueryCompilationContext).QuerySplittingBehavior)
                                    .Visit(projectionsSelect);

                                projections = ((SelectExpression)projectionsSelect.QueryExpression).Projection;
                            }

                            var nonQuery = TranslateExecuteDelete(shapedQueryExpression);
                            var delete = (DeleteExpression)nonQuery!.Expression;

                            new ReturningDeleteExpression(
                                    delete,
                                    projections)
                                .SaveInto(QueryCompilationContext);
                        }

                        var stubSelectSource = Visit(methodCallExpression.Arguments[2]);

                        var stubSelect = TranslateSelect(
                            (ShapedQueryExpression)stubSelectSource,
                            UnwrapLambdaFromQuote(methodCallExpression.Arguments[1]));

                        return stubSelect;
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


    public static LambdaExpression UnwrapLambdaFromQuote(Expression expression)
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
}