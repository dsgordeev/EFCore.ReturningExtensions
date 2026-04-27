using System.Linq.Expressions;
using System.Reflection;
using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

// ReSharper disable once CheckNamespace
namespace EFCore.ReturningExtensions.NonQueryPatch;

public static class RelationalQueryableExtensionsPatchExtensions
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

    public static readonly MethodInfo ExecuteDeleteMethodInfoMarker
        = typeof(RelationalQueryableExtensionsPatchExtensions).GetTypeInfo().GetDeclaredMethod(nameof(ExecuteDelete))!;

    public static int ExecuteUpdate<TSource>(
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

    public static readonly MethodInfo ExecuteUpdateMethodInfoMarker
        = typeof(RelationalQueryableExtensionsPatchExtensions).GetTypeInfo().GetDeclaredMethod(nameof(ExecuteUpdate))!;

    public static IRelationalCommand RentAndPopulateRelationalCommand(
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