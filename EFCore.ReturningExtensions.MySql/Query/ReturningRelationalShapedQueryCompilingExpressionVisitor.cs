// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Modified by Gordeev Dmitry in 2026 for EFCore.ReturningExtensions.
// Original source: Entity Framework Core (https://github.com/dotnet/efcore), licensed under the MIT License.

using System.Linq.Expressions;
using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using static System.Linq.Expressions.Expression;

namespace EFCore.ReturningExtensions.MySql.Query;

internal class ReturningRelationalShapedQueryCompilingExpressionVisitorFactory(
    ShapedQueryCompilingExpressionVisitorDependencies dependencies,
    RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies)
    : IShapedQueryCompilingExpressionVisitorFactory
{
    public virtual ShapedQueryCompilingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new ReturningRelationalShapedQueryCompilingExpressionVisitor(
            dependencies,
            relationalDependencies,
            queryCompilationContext);
}

public partial class ReturningRelationalShapedQueryCompilingExpressionVisitor : RelationalShapedQueryCompilingExpressionVisitor
{
    private readonly Type _contextType;
    private readonly ISet<string> _tags;
    private readonly bool _threadSafetyChecksEnabled;
    private readonly bool _detailedErrorsEnabled;
    private readonly bool _useRelationalNulls;

    /// <summary>
    ///     Creates a new instance of the <see cref="ShapedQueryCompilingExpressionVisitor" /> class.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this class.</param>
    /// <param name="relationalDependencies">Parameter object containing relational dependencies for this class.</param>
    /// <param name="queryCompilationContext">The query compilation context object to use.</param>
    public ReturningRelationalShapedQueryCompilingExpressionVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _contextType = queryCompilationContext.ContextType;
        _tags = queryCompilationContext.Tags;
        _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
        _detailedErrorsEnabled = dependencies.CoreSingletonOptions.AreDetailedErrorsEnabled;
        _useRelationalNulls = RelationalOptionsExtension.Extract(queryCompilationContext.ContextOptions).UseRelationalNulls;
    }

    /// <inheritdoc />
    protected override Expression VisitExtension(Expression extensionExpression)
        => extensionExpression is NonQueryExpression nonQueryExpression
            ? VisitNonQuery(nonQueryExpression)
            : base.VisitExtension(extensionExpression);

    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        var (match, selectExpression) = shapedQueryExpression.QueryExpression switch
        {
            ReturningUpdateExpression update => (true, update.UpdateExpression.SelectExpression),
            ReturningDeleteExpression update => (true, update.DeleteExpression.SelectExpression),
            _ => (false, (SelectExpression)shapedQueryExpression.QueryExpression)
        };

        if (!match)
        {
            return base.VisitShapedQuery(shapedQueryExpression);
        }

        VerifyNoClientConstant(shapedQueryExpression.ShaperExpression);
        var querySplittingBehavior = ((RelationalQueryCompilationContext)QueryCompilationContext).QuerySplittingBehavior;
        var splitQuery = querySplittingBehavior == QuerySplittingBehavior.SplitQuery;
        var collectionCount = 0;

        {
            var nonComposedFromSql = selectExpression.IsNonComposedFromSql();
            var shaper = new ShaperProcessingExpressionVisitor2(this, selectExpression, _tags, splitQuery, nonComposedFromSql).ProcessShaper(
                shapedQueryExpression.ShaperExpression,
                out var relationalCommandCache, out var readerColumns, out _, ref collectionCount);

            relationalCommandCache = new RelationalCommandCache(
                Dependencies.MemoryCache,
                RelationalDependencies.QuerySqlGeneratorFactory,
                RelationalDependencies.RelationalParameterBasedSqlProcessorFactory,
                shapedQueryExpression.QueryExpression,
                _useRelationalNulls);

            return New(
                typeof(SingleQueryingEnumerable<>).MakeGenericType(shaper.ReturnType).GetConstructors()[0],
                Convert(QueryCompilationContext.QueryContextParameter, typeof(RelationalQueryContext)),
                Constant(relationalCommandCache),
                Constant(readerColumns, typeof(IReadOnlyList<ReaderColumn?>)),
                Constant(shaper.Compile()),
                Constant(_contextType),
                Constant(
                    QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.NoTrackingWithIdentityResolution),
                Constant(_detailedErrorsEnabled),
                Constant(_threadSafetyChecksEnabled));
        }
    }
}