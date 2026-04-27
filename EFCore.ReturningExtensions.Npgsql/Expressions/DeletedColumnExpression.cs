using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System.Linq.Expressions;

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

    public override string Name { get; }
    public override TableExpressionBase Table { get; }
    public override string TableAlias { get; }
    public override bool IsNullable { get; }
}

public sealed class PgDeleteExpression : Expression, IPrintableExpression
{
    /// <summary>
    ///     The tables that rows are to be deleted from.
    /// </summary>
    public TableExpression Table { get; }

    /// <summary>
    ///     Additional tables which can be referenced in the predicate.
    /// </summary>
    public IReadOnlyList<TableExpressionBase> FromItems { get; }

    /// <summary>
    ///     The WHERE predicate for the DELETE.
    /// </summary>
    public SqlExpression? Predicate { get; }

    /// <summary>
    ///     The list of tags applied to this <see cref="DeleteExpression" />.
    /// </summary>
    public ISet<string> Tags { get; }

    /// <summary>
    ///     Creates a new instance of the <see cref="PgDeleteExpression" /> class.
    /// </summary>
    public PgDeleteExpression(
        TableExpression table,
        IReadOnlyList<TableExpressionBase> fromItems,
        SqlExpression? predicate,
        ISet<string> tags)
    {
        (Table, FromItems, Predicate, Tags) = (table, fromItems, predicate, tags);
    }

    /// <inheritdoc />
    public override Type Type
        => typeof(object);

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => Predicate is null
            ? this
            : Update((SqlExpression?)visitor.Visit(Predicate));

    /// <summary>
    ///     Creates a new expression that is like this one, but using the supplied children. If all of the children are the same, it will
    ///     return this expression.
    /// </summary>
    /// <param name="predicate">The <see cref="Predicate" /> property of the result.</param>
    public PgDeleteExpression Update(SqlExpression? predicate)
        => predicate == Predicate
            ? this
            : new PgDeleteExpression(Table, FromItems, predicate, Tags);

    /// <inheritdoc />
    public void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.AppendLine($"DELETE FROM {Table.Name} AS {Table.Alias}");

        if (FromItems.Count > 0)
        {
            var first = true;
            foreach (var fromItem in FromItems)
            {
                if (first)
                {
                    expressionPrinter.Append("USING ");
                    first = false;
                }
                else
                {
                    expressionPrinter.Append(", ");
                }

                expressionPrinter.Visit(fromItem);
            }
        }

        if (Predicate is not null)
        {
            expressionPrinter.Append("WHERE ");
            expressionPrinter.Visit(Predicate);
        }
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
            && (ReferenceEquals(this, obj)
                || obj is PgDeleteExpression pgDeleteExpression
                && Equals(pgDeleteExpression));

    private bool Equals(PgDeleteExpression pgDeleteExpression)
        => Table == pgDeleteExpression.Table
            && FromItems.SequenceEqual(pgDeleteExpression.FromItems)
            && (Predicate is null ? pgDeleteExpression.Predicate is null : Predicate.Equals(pgDeleteExpression.Predicate));

    /// <inheritdoc />
    public override int GetHashCode()
        => Table.GetHashCode();
}
