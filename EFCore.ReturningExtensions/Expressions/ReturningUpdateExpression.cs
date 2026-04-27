using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System.Diagnostics;
using System.Linq.Expressions;

namespace EFCore.ReturningExtensions.Expressions;

[DebuggerDisplay("{Microsoft.EntityFrameworkCore.Query.ExpressionPrinter.Print(this), nq}")]
public class ReturningUpdateExpression(
    UpdateExpression updateExpression,
    IReadOnlyList<ProjectionExpression> projections,
    Type type)
    : Expression, IPrintableExpression
{
    public override Type Type => type;

    public override ExpressionType NodeType => ExpressionType.Extension;

    public UpdateExpression UpdateExpression { get; } = updateExpression;

    public IReadOnlyList<ProjectionExpression> Projections { get; } = projections;

    public void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Visit(UpdateExpression);
        expressionPrinter.AppendLine();

        expressionPrinter.Append("WITH RETURNING ");
        if (Projections.Count > 0)
        {
            expressionPrinter.Visit(Projections[0]);
            foreach (var projection in Projections.Skip(1))
            {
                expressionPrinter.AppendLine(",");
                expressionPrinter.Visit(projection);
            }
        }
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;

    public ReturningUpdateExpression Update(UpdateExpression updateExpression, IReadOnlyList<ProjectionExpression> projections)
        => !UpdateExpression.Equals(updateExpression) || !Projections.SequenceEqual(projections)
            ? new ReturningUpdateExpression(updateExpression, projections, Type)
            : this;

    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is ReturningUpdateExpression expression
               && Equals(expression));

    private bool Equals(ReturningUpdateExpression expression)
        => UpdateExpression.Equals(expression.UpdateExpression)
           && Projections.SequenceEqual(expression.Projections);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(UpdateExpression);
        foreach (var item in Projections)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}