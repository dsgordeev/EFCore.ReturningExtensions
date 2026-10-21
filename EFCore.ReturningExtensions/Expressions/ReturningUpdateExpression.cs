using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System.Linq.Expressions;

namespace EFCore.ReturningExtensions.Expressions;

public class ReturningUpdateExpression(
    UpdateExpression updateExpression,
    IReadOnlyList<ProjectionExpression> projections,
    Type type)
    : Expression, IPrintableExpression
{
    public override Type Type => type;

    public override ExpressionType NodeType => ExpressionType.Extension;

    public UpdateExpression UpdateExpression { get; set; } = updateExpression;
  
    public IReadOnlyList<ProjectionExpression> Projections { get; } = projections;

    public void Print(ExpressionPrinter expressionPrinter)
    {
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;

    public ReturningUpdateExpression Update(UpdateExpression updateExpression, IReadOnlyList<ProjectionExpression> projections)
        => !UpdateExpression.Equals(updateExpression) || !Projections.SequenceEqual(projections)
            ? new ReturningUpdateExpression(updateExpression, projections, Type)
            : this;
}