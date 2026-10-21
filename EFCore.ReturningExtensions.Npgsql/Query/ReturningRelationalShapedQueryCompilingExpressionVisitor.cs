// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Modified by Gordeev Dmitry in 2026 for EFCore.ReturningExtensions.
// Original source: Entity Framework Core (https://github.com/dotnet/efcore), licensed under the MIT License.

using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using static System.Linq.Expressions.Expression;

namespace EFCore.ReturningExtensions.Npgsql.Query;

public class ReturningRelationalShapedQueryCompilingVisitorFactory(
    ShapedQueryCompilingExpressionVisitorDependencies dependencies,
    RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies)
    : IShapedQueryCompilingExpressionVisitorFactory
{
    [DebuggerStepThrough]
    public ShapedQueryCompilingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new ReturningRelationalShapedQueryCompilingExpressionVisitor(
            dependencies,
            relationalDependencies,
            queryCompilationContext);
}


public partial class ReturningRelationalShapedQueryCompilingExpressionVisitor(
    ShapedQueryCompilingExpressionVisitorDependencies dependencies,
    RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies,
    QueryCompilationContext queryCompilationContext)
    : RelationalShapedQueryCompilingExpressionVisitor(dependencies, relationalDependencies, queryCompilationContext)
{
    private readonly Type _contextType = queryCompilationContext.ContextType;
    private readonly ISet<string> _tags = queryCompilationContext.Tags;
    private readonly bool _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
    private readonly bool _detailedErrorsEnabled = dependencies.CoreSingletonOptions.AreDetailedErrorsEnabled;

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
            var shaper = new ShaperProcessingExpressionVisitor2(this, selectExpression, _tags, splitQuery, nonComposedFromSql)
                .ProcessShaper(
                    shapedQueryExpression.ShaperExpression, out _, out var readerColumns,
                    out _, ref collectionCount);

            var readerColumnsExpression = CreateReaderColumnsExpression(readerColumns, Dependencies.LiftableConstantFactory);

            var relationalCommandResolver = CreateRelationalCommandResolverExpression(shapedQueryExpression.QueryExpression);

            return Call(
                CreateSingleQueryingEnumerableMethodInfo.MakeGenericMethod(shaper.ReturnType),
                Convert(QueryCompilationContext.QueryContextParameter, typeof(RelationalQueryContext)),
                relationalCommandResolver,
                readerColumnsExpression,
                shaper,
                Constant(_contextType),
                Constant(QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.NoTrackingWithIdentityResolution),
                Constant(_detailedErrorsEnabled),
                Constant(_threadSafetyChecksEnabled));
        }
    }

    private static readonly MethodInfo CreateSingleQueryingEnumerableMethodInfo
        = typeof(SingleQueryingEnumerable)
            .GetMethod(nameof(SingleQueryingEnumerable.Create))!;

    private static Expression CreateReaderColumnsExpression(
        IReadOnlyList<ReaderColumn?>? readerColumns,
        ILiftableConstantFactory liftableConstantFactory)
    {
        if (readerColumns is null)
        {
            return Constant(readerColumns, typeof(ReaderColumn?[]));
        }

        var materializerLiftableConstantContextParameter = Parameter(typeof(MaterializerLiftableConstantContext));
        var initializers = new List<Expression>();

        foreach (var readerColumn in readerColumns)
        {
            var currentReaderColumn = readerColumn;
            if (currentReaderColumn is null)
            {
                initializers.Add(Constant(null, typeof(ReaderColumn)));
                continue;
            }

            var propertyExpression = LiftableConstantExpressionHelpers.BuildMemberAccessForProperty(
                currentReaderColumn.Property,
                materializerLiftableConstantContextParameter);

            initializers.Add(
                New(
                    ReaderColumn.GetConstructor(currentReaderColumn.Type),
                    Constant(currentReaderColumn.IsNullable),
                    Constant(currentReaderColumn.Name, typeof(string)),
                    propertyExpression,
                    currentReaderColumn.GetFieldValueExpression));
        }

        var result = liftableConstantFactory.CreateLiftableConstant(
            readerColumns,
            Lambda<Func<MaterializerLiftableConstantContext, object>>(
                NewArrayInit(
                    typeof(ReaderColumn),
                    initializers),
                materializerLiftableConstantContextParameter),
            "readerColumns",
            typeof(ReaderColumn[]));

        return result;
    }

    /// <summary>
    ///     Called after a structural type is materialized, but before it's handed off to the change tracker.
    ///     Here we inject the JSON shapers for any complex JSON properties the type has.
    /// </summary>
    /// <remarks>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </remarks>
    public override void AddStructuralTypeInitialization(
        StructuralTypeShaperExpression shaper,
        ParameterExpression instanceVariable,
        List<ParameterExpression> variables,
        List<Expression> expressions)
    {
        if (_currentShaperProcessor != null)
        {
            _currentShaperProcessor!.ProcessTopLevelComplexJsonProperties(shaper, instanceVariable, expressions);
        }
        else
        {
            var p = (ShaperProcessingExpressionVisitor)Processor.GetValue(this)!;

            Process.Invoke(p, [shaper, instanceVariable, expressions]);
        }
    }

    private static readonly FieldInfo Processor = typeof(ReturningRelationalShapedQueryCompilingExpressionVisitor).BaseType!.GetField("_currentShaperProcessor",
        BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo Process = typeof(ShaperProcessingExpressionVisitor).GetMethod("ProcessTopLevelComplexJsonProperties",
        BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo Mi = typeof(ReturningRelationalShapedQueryCompilingExpressionVisitor).BaseType!.GetMethod(nameof(CreateRelationalCommandResolverExpression),
        BindingFlags.NonPublic | BindingFlags.Instance)!;

    private Expression CreateRelationalCommandResolverExpression(Expression queryExpression)
    {
        return (Expression)Mi.Invoke(this, [queryExpression])!;
    }
}