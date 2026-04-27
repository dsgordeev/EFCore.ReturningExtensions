using System.Linq.Expressions;
using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.Expressions;

public class ReturningUpdateExpression : Expression, IPrintableExpression
{
    public ReturningUpdateExpression(UpdateExpression updateExpression,
        IReadOnlyList<ProjectionExpression> projections)
    {
        UpdateExpression = updateExpression;
        Projections = projections;
    }

    public UpdateExpression UpdateExpression { get; set; }
    public IReadOnlyList<ProjectionExpression> Projections { get; }

    public void Print(ExpressionPrinter expressionPrinter)
    {
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;
}