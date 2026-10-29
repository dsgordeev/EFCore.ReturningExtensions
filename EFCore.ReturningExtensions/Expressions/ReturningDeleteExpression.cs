using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.Expressions;

public class ReturningDeleteExpression(
    DeleteExpression deleteExpression,
    IReadOnlyList<ProjectionExpression> projections,
    Type type)
    : Expression, IPrintableExpression
{
    public override Type Type => type;

    public override ExpressionType NodeType => ExpressionType.Extension;

    public DeleteExpression DeleteExpression { get; set; } = deleteExpression;

    public IReadOnlyList<ProjectionExpression> Projections { get; } = projections;

    public void Print(ExpressionPrinter expressionPrinter)
    {
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;

    public ReturningDeleteExpression Update(DeleteExpression deleteExpression, IReadOnlyList<ProjectionExpression> projections)
        => !deleteExpression.Equals(DeleteExpression) || !Projections.SequenceEqual(projections)
            ? new ReturningDeleteExpression(deleteExpression, projections, Type)
            : this;
}