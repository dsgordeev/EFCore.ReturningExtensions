using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.Expressions;

public class ReturningDeleteExpression(
    DeleteExpression deleteExpression,
    IReadOnlyList<ProjectionExpression> projections)
    : Expression, IPrintableExpression
{
    public DeleteExpression DeleteExpression { get; set; } = deleteExpression;
    public IReadOnlyList<ProjectionExpression> Projections { get; } = projections;

    public void Print(ExpressionPrinter expressionPrinter)
    {
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;
}