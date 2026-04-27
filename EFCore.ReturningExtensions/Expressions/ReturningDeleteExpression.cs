using System.Linq.Expressions;
using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.Expressions;

public class ReturningDeleteExpression : Expression, IPrintableExpression
{
    public ReturningDeleteExpression(DeleteExpression deleteExpression,
        IReadOnlyList<ProjectionExpression> projections)
    {
        DeleteExpression = deleteExpression;
        Projections = projections;
    }

    public DeleteExpression DeleteExpression { get; set; }
    public IReadOnlyList<ProjectionExpression> Projections { get; }

    public void Print(ExpressionPrinter expressionPrinter)
    {
    }

    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;
}