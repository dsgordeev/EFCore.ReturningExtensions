using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.Sqlite.Query.Extensions;

internal static class ReturningExpressionInjectExtensions
{
    public static SelectExpression AppendExpressionAnnotation<T>(this SelectExpression origin, T expression)
    {
        origin.AddAnnotation(typeof(T).Name, expression);
        
        return origin;
    }
}