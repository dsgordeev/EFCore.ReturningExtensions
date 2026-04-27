using System.Linq.Expressions;
using EFCore.ReturningExtensions.Expressions;
using EFCore.ReturningExtensions.Npgsql.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.Npgsql.Extensions;

internal static class ReturningExtensions
{
    public static void SaveInto(this ReturningUpdateExpression expression, QueryCompilationContext queryCompilationContext)
    {
        var key = Guid.NewGuid();

        queryCompilationContext.AddTag(key.ToString());

        ReturningUpdateExpressionCache.Add(key, expression);
    }

    public static void SaveInto(this ReturningDeleteExpression expression, QueryCompilationContext queryCompilationContext)
    {
        var key = Guid.NewGuid();

        queryCompilationContext.AddTag(key.ToString());

        ReturningDeleteExpressionCache.Add(key, expression);
    }

    public static Expression? ExtractReturningExpression(this Expression expression)
    {
        if (expression is SelectExpression select)
        {
            foreach (var tag in select.Tags)
            {
                if (Guid.TryParse(tag, out var key))
                {
                    if (ReturningUpdateExpressionCache.Get(key) is { } returningUpdateExpression)
                    {
                        return returningUpdateExpression;
                    }

                    if (ReturningDeleteExpressionCache.Get(key) is { } returningDeleteExpression)
                    {
                        return returningDeleteExpression;
                    }
                }
            }
        }

        return null;
    }
}