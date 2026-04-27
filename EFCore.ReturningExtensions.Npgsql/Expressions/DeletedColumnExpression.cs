using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ReturningExtensions.Npgsql.Expressions;

internal class DeletedColumnExpression : ColumnExpression
{
    private readonly ColumnExpression _columnExpression;

    public DeletedColumnExpression(string tableAlias, ColumnExpression columnExpression) : base(
        columnExpression.Type,
        columnExpression.TypeMapping)
    {
        _columnExpression = columnExpression;

        Name = _columnExpression.Name;
        Table = _columnExpression.Table;
        TableAlias = tableAlias;
        IsNullable = _columnExpression.IsNullable;
    }

    public override DeletedColumnExpression MakeNullable() =>
        IsNullable
            ? this
            : new DeletedColumnExpression(TableAlias, _columnExpression.MakeNullable());

    public override SqlExpression ApplyTypeMapping(RelationalTypeMapping? typeMapping)
    {
        return new DeletedColumnExpression(TableAlias, (ColumnExpression)_columnExpression.ApplyTypeMapping(typeMapping));
    }


    public override string Name { get; }
    public override TableExpressionBase Table { get; }
    public override string TableAlias { get; }
    public override bool IsNullable { get; }
}
