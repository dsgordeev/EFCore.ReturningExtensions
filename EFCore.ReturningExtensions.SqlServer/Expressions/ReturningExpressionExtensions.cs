using System.Linq.Expressions;
using System.Security.Cryptography.X509Certificates;
using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.SqlServer.Expressions;

internal static class ReturningExpressionExtensions
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
                        select.ApplyTags(select.Tags.Except(new HashSet<string> { tag }).ToHashSet());

                        return returningUpdateExpression;
                    }

                    if (ReturningDeleteExpressionCache.Get(key) is { } returningDeleteExpression)
                    {
                        select.ApplyTags(select.Tags.Except(new HashSet<string> { tag }).ToHashSet());

                        return returningDeleteExpression;
                    }
                }
            }
        }

        return null;
    }
}