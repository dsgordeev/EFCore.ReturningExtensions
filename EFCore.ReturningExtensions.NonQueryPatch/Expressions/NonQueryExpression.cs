using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.ReturningExtensions.NonQueryPatch.Expressions;

public class NonQueryExpression : Expression, IPrintableExpression
{
    /// <summary>
    ///     Creates a new instance of the <see cref="NonQueryExpression" /> class with associated query expression and command source.
    /// </summary>
    /// <param name="expression">The expression to affect rows on the server.</param>
    /// <param name="commandSource">The command source to use for this non-query operation.</param>
    public NonQueryExpression(Expression expression, CommandSource commandSource)
    {
        Expression = expression;
        CommandSource = commandSource;
    }

    /// <summary>
    ///     Creates a new instance of the <see cref="NonQueryExpression" /> class with associated delete expression.
    /// </summary>
    /// <param name="deleteExpression">The delete expression to delete rows on the server.</param>
    public NonQueryExpression(DeleteExpression deleteExpression)
        : this(deleteExpression, (CommandSource)9)
    {
    }

    public NonQueryExpression(UpdateExpression updateExpression)
        : this(updateExpression, (CommandSource)10)
    {
    }

    /// <summary>
    ///     An expression representing the non-query operation to be run against server.
    /// </summary>
    public virtual Expression Expression { get; }

    /// <summary>
    ///     The command source to use for this non-query operation.
    /// </summary>
    public virtual CommandSource CommandSource { get; }

    /// <inheritdoc />
    public override Type Type
        => typeof(int);

    /// <inheritdoc />
    public sealed override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var expression = visitor.Visit(Expression);

        return Update(expression);
    }

    /// <summary>
    ///     Creates a new expression that is like this one, but using the supplied children. If all of the children are the same, it will
    ///     return this expression.
    /// </summary>
    /// <param name="expression">The <see cref="Expression" /> property of the result.</param>
    /// <returns>This expression if no children changed, or an expression with the updated children.</returns>
    public virtual NonQueryExpression Update(Expression expression)
        => expression != Expression
            ? new NonQueryExpression(expression, CommandSource)
            : this;

    /// <inheritdoc />
    public virtual void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append($"({nameof(NonQueryExpression)}: ");
        expressionPrinter.Visit(Expression);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is NonQueryExpression nonQueryExpression
               && Equals(nonQueryExpression));

    private bool Equals(NonQueryExpression nonQueryExpression)
        => Expression == nonQueryExpression.Expression;

    /// <inheritdoc />
    public override int GetHashCode()
        => Expression.GetHashCode();
}