using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.Extensions;

public static class ReturningDeleteExpressionExtensions
{
    public static readonly MethodInfo InternalDeletedMethodInfo = typeof(ReturningDeleteExpressionExtensions).GetTypeInfo()
        .GetDeclaredMethod(nameof(InternalDeleted))!;

    public static IQueryable<TResult> Deleted<TSource, TResult>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, TResult>> selector)
    {
        return source.InternalDeleted(selector, source, source);
    }

    internal static IQueryable<TResult> InternalDeleted<TSource, TResult>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, TResult>> selector,
        IQueryable<TSource> stubSelectSource,
        IQueryable<TSource> projectionsSource)
    {
        var methodCallExpression = Expression.Call(
            method: InternalDeletedMethodInfo.MakeGenericMethod(typeof(TSource), typeof(TResult)),
            arg0: source.Expression,
            arg1: Expression.Quote(selector),
            arg2: stubSelectSource.Expression,
            arg3: projectionsSource.Expression);

        return source.Provider is IAsyncQueryProvider provider
            ? provider.CreateQuery<TResult>(methodCallExpression)
            : throw new InvalidOperationException();
    }
}