using System.Linq.Expressions;
using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Npgsql.Extensions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace EFCore.ReturningExtensions.Npgsql.Query;

internal class NpgsqlReturningQueryMethodTranslatingExpressionVisitorFactory(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
    INpgsqlSingletonOptions npgsqlSingletonOptions)
    : IQueryableMethodTranslatingExpressionVisitorFactory
{
    public QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
    {
        return new NpgsqlReturningQueryMethodTranslatingExpressionVisitor(
            dependencies,
            relationalDependencies,
            (NpgsqlQueryCompilationContext)queryCompilationContext,
            npgsqlSingletonOptions);
    }
}

internal class NpgsqlReturningQueryMethodTranslatingExpressionVisitor(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
    NpgsqlQueryCompilationContext queryCompilationContext,
    INpgsqlSingletonOptions sqlServerSingletonOptions)
    : NpgsqlQueryableMethodTranslatingExpressionVisitor(
        dependencies,
        relationalDependencies,
        queryCompilationContext,
        sqlServerSingletonOptions)
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
                        {
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

                            var newArrayExpression = (NewArrayExpression)methodCallExpression.Arguments[1];
                            var array = new ExecuteUpdateSetter[newArrayExpression.Expressions.Count];
                            for (var i = 0; i < array.Length; i++)
                            {
                                var obj = (NewExpression)newArrayExpression.Expressions[i];
                                var propertySelector = (LambdaExpression)obj.Arguments[0];
                                var expression2 = obj.Arguments[1];
                                if (expression2 is UnaryExpression { NodeType: ExpressionType.Convert } unaryExpression)
                                {
                                    var operand = unaryExpression.Operand;
                                    if (expression2.Type == typeof(object))
                                    {
                                        expression2 = operand;
                                    }
                                }
                                array[i] = new ExecuteUpdateSetter(propertySelector, expression2);
                            }

                            var update = TranslateExecuteUpdate(shapedQueryExpression, array)!;

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

                            var delete = TranslateExecuteDelete(shapedQueryExpression)!;

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

    public static LambdaExpression UnwrapLambdaFromQuote(Expression expression)
        => (LambdaExpression)(expression is UnaryExpression unary && expression.NodeType == ExpressionType.Quote
            ? unary.Operand
            : expression);
}