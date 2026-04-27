using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.Extensions;

public static class ReturningUpdateExpressionExtensions
{
    public static readonly MethodInfo InternalUpdatedMethodInfo
        = typeof(ReturningUpdateExpressionExtensions)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == nameof(InternalUpdated) && m.GetParameters()[1].ParameterType == typeof(IReadOnlyList<ITuple>));

    public static IQueryable<TResult> Updated<TSource, TResult>(
        this IQueryable<TSource> source,
        Action<UpdateSettersBuilder<TSource>> setPropertyCalls,
        Expression<Func<TSource, TResult>> selector)
    {
        var setterBuilder = new UpdateSettersBuilder<TSource>();
        setPropertyCalls(setterBuilder);
        var setters = setterBuilder.BuildSettersExpression();

        return InternalUpdated(source, setters, selector, source, source);
    }

    internal static IQueryable<TResult> InternalUpdated<TSource, TResult>(
        this IQueryable<TSource> source,
        NewArrayExpression setters,
        Expression<Func<TSource, TResult>> selector,
        IQueryable<TSource> stubSelectSource,
        IQueryable<TSource> projectionsSource)
    {
        var methodCallExpression = Expression.Call(
            method: InternalUpdatedMethodInfo.MakeGenericMethod(typeof(TSource), typeof(TResult)),
            arg0: source.Expression,
            arg1: setters,
            arg2: Expression.Quote(selector),
            arg3: stubSelectSource.Expression,
            arg4: projectionsSource.Expression);

        return source.Provider is IAsyncQueryProvider provider
            ? provider.CreateQuery<TResult>(methodCallExpression)
            : throw new InvalidOperationException();
    }

    private static int InternalUpdated<TSource, TResult>(
        this IQueryable<TSource> source,
        [NotParameterized] IReadOnlyList<ITuple> setters,
        Expression<Func<TSource, TResult>> selector,
        IQueryable<TSource> stubSelectSource,
        IQueryable<TSource> projectionsSource)
        => throw new UnreachableException("Can't call this overload directly");

}