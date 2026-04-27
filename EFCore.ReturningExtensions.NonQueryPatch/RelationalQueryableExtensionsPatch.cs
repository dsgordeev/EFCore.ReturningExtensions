using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;




#if NET7_0
#else
using EFCore.ReturningExtensions.NonQueryPatch.Infrastructure;
#endif

// ReSharper disable once CheckNamespace
namespace EFCore.ReturningExtensions.NonQueryPatch;

public static class RelationalQueryableExtensionsPatch
{
    public static int ExecuteDelete<TSource>(this IQueryable<TSource> source)
    {
        return source.Provider.Execute<int>(Expression.Call(ExecuteDeleteMethodInfoMarker.MakeGenericMethod(typeof(TSource)),
            source.Expression));
    }

    public static Task<int> ExecuteDeleteAsync<TSource>(this IQueryable<TSource> source, CancellationToken token)
    {
        return source.Provider is IAsyncQueryProvider provider
            ? provider.ExecuteAsync<Task<int>>(
                Expression.Call(ExecuteDeleteMethodInfoMarker.MakeGenericMethod(typeof(TSource)), source.Expression),
                token)
            : throw new InvalidOperationException(CoreStrings.IQueryableProviderNotAsync);
    }

    internal static readonly MethodInfo ExecuteDeleteMethodInfoMarker
        = typeof(RelationalQueryableExtensionsPatch).GetTypeInfo().GetDeclaredMethod(nameof(ExecuteDelete))!;

    public static int ExecuteUpdate2<TSource>(
        this IQueryable<TSource> source,
        Expression<Func<SetPropertyCalls<TSource>, SetPropertyCalls<TSource>>> setPropertyCalls)
        => source.Provider.Execute<int>(
            Expression.Call(ExecuteUpdateMethodInfoMarker.MakeGenericMethod(typeof(TSource)), source.Expression, setPropertyCalls));

    public static Task<int> ExecuteUpdateAsync<TSource>(
        this IQueryable<TSource> source,
        Expression<Func<SetPropertyCalls<TSource>, SetPropertyCalls<TSource>>> setPropertyCalls,
        CancellationToken cancellationToken = default)
        => source.Provider is IAsyncQueryProvider provider
            ? provider.ExecuteAsync<Task<int>>(
                Expression.Call(
                    ExecuteUpdateMethodInfoMarker.MakeGenericMethod(typeof(TSource)), source.Expression, setPropertyCalls), cancellationToken)
            : throw new InvalidOperationException(CoreStrings.IQueryableProviderNotAsync);

    internal static readonly MethodInfo ExecuteUpdateMethodInfoMarker
        = typeof(RelationalQueryableExtensionsPatch).GetTypeInfo().GetDeclaredMethod(nameof(ExecuteUpdate2))!;

    internal static IRelationalCommand RentAndPopulateRelationalCommand(
        this RelationalCommandCachePatch relationalCommandCache,
        RelationalQueryContext queryContext)
    {
        var relationalCommandTemplate =
            relationalCommandCache.GetRelationalCommandTemplate(queryContext.ParameterValues);
        var relationalCommand = queryContext.Connection.RentCommand();
        relationalCommand.PopulateFrom(relationalCommandTemplate);
        return relationalCommand;
    }
}