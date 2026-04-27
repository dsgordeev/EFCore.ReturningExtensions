using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.Expressions;

public class ReturningUpdateExpression(
    UpdateExpression updateExpression,
    IReadOnlyList<ProjectionExpression> projections)
    : Expression, IPrintableExpression
{
    public UpdateExpression UpdateExpression { get; set; } = updateExpression;
    public IReadOnlyList<ProjectionExpression> Projections { get; } = projections;

    public void Print(ExpressionPrinter expressionPrinter)
    {
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;
}