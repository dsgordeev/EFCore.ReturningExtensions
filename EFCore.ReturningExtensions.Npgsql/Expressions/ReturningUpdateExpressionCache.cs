using EFCore.ReturningExtensions.Expressions;

namespace EFCore.ReturningExtensions.Npgsql.Expressions;

internal static class ReturningUpdateExpressionCache
{
    private static readonly Dictionary<Guid, ReturningUpdateExpression> Dict = [];

    public static void Add(Guid key, ReturningUpdateExpression expr) => Dict[key] = expr;

    public static ReturningUpdateExpression? Get(Guid key) => Dict.Remove(key, out var expr) ? expr : null;
}