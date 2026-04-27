using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.MySql.Expressions;

internal class CustomTableAliasColumnExpression : ColumnExpression
{
    private readonly ColumnExpression _columnExpression;

    public CustomTableAliasColumnExpression(string tableAlias, ColumnExpression columnExpression) : base(
        columnExpression.Type,
        columnExpression.TypeMapping)
    {
        _columnExpression = columnExpression;

        Name = _columnExpression.Name;
        try
        {
            Table = _columnExpression.Table;
        }
        catch (Exception)
        {
            Table = null!;
        }
        TableAlias = tableAlias;
        IsNullable = _columnExpression.IsNullable;
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        return this;
    }

    public override CustomTableAliasColumnExpression MakeNullable() =>
        IsNullable
            ? this
            : new CustomTableAliasColumnExpression(TableAlias, _columnExpression.MakeNullable());

    public override string Name { get; }
    public override TableExpressionBase Table { get; }
    public override string TableAlias { get; }
    public override bool IsNullable { get; }
}