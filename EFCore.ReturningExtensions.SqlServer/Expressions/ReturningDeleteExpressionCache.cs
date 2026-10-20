using EFCore.ReturningExtensions.Expressions;

namespace EFCore.ReturningExtensions.SqlServer.Expressions;

internal static class ReturningDeleteExpressionCache
{
    private static readonly Dictionary<Guid, ReturningDeleteExpression> Dict = [];

    public static void Add(Guid key, ReturningDeleteExpression expr) => Dict[key] = expr;

    public static ReturningDeleteExpression? Get(Guid key) => Dict.Remove(key, out var expr) ? expr : null;
}