using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Sqlite.Query.Internal;
using System.Linq.Expressions;
using System.Reflection;
using EFCore.ReturningExtensions.Sqlite.Query.Extensions;

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
            (RelationalQueryCompilationContext)queryCompilationContext);
    }
}

internal class SqliteReturningQueryMethodTranslatingExpressionVisitor(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
    RelationalQueryCompilationContext queryCompilationContext) :
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
                             && genericMethod == ReturningUpdateExpressionExtensions.InternalUpdatedMethodInfo
                             || x == ReturningUpdateExpressionExtensions.InternalUpdated2MethodInfo.Name
                             && genericMethod == ReturningUpdateExpressionExtensions.InternalUpdated2MethodInfo:
                        IReadOnlyList<ProjectionExpression> projections;
                        {
                            var projectionsSource = Visit(methodCallExpression.Arguments[4]);

                            var projectionsSelect = TranslateSelect(
                                (ShapedQueryExpression)projectionsSource,
                                UnwrapLambdaFromQuote(methodCallExpression.Arguments[2]));

                            new SelectExpressionProjectionApplyingExpressionVisitor(((RelationalQueryCompilationContext)QueryCompilationContext).QuerySplittingBehavior)
                                .Visit(projectionsSelect);

                            projections = ((SelectExpression)projectionsSelect.QueryExpression).Projection;
                        }

                        var setPropertyCalls = UnwrapLambdaFromQuote(methodCallExpression.Arguments[1]);
                        var nonQuery = TranslateExecuteUpdate(shapedQueryExpression, setPropertyCalls);
                        var update = (UpdateExpression)nonQuery!.Expression;

                        var returningUpdateExpression = new ReturningUpdateExpression(
                            update,
                            projections);

                        var stubSelectSource = Visit(methodCallExpression.Arguments[3]);

                        var stubSelect = TranslateSelect(
                            (ShapedQueryExpression)stubSelectSource,
                            UnwrapLambdaFromQuote(methodCallExpression.Arguments[2]));

                        var enrichedSelectExpression = ((SelectExpression)stubSelect.QueryExpression)
                            .AppendExpressionAnnotation(returningUpdateExpression);

                        return stubSelect.UpdateQueryExpression(enrichedSelectExpression);
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

                        var returningDeleteExpression = new ReturningDeleteExpression(
                                                    delete,
                                                    projections);

                        var stubSelectSource = Visit(methodCallExpression.Arguments[2]);

                        var stubSelect = TranslateSelect(
                            (ShapedQueryExpression)stubSelectSource,
                            UnwrapLambdaFromQuote(methodCallExpression.Arguments[1]));

                        var enrichedSelectExpression = ((SelectExpression)stubSelect.QueryExpression)
                            .AppendExpressionAnnotation(returningDeleteExpression);

                        return stubSelect.UpdateQueryExpression(enrichedSelectExpression);
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
}