using System.Linq.Expressions;
using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.Sqlite.Query.Extensions;

internal static class ReturningExpressionExtractExtensions
{
    public static Expression? ExtractReturningExpression(this Expression expression)
    {
        if (expression is SelectExpression select)
        {
            if (select.FindAnnotation(nameof(ReturningUpdateExpression)) is
                {
                    Value: ReturningUpdateExpression returningUpdateExpression
                })
            {
                return returningUpdateExpression;
            }

            if (select.FindAnnotation(nameof(ReturningDeleteExpression)) is
                {
                    Value: ReturningDeleteExpression returningDeleteExpression
                })
            {
                return returningDeleteExpression;
            }
        }

        return null;
    }
}