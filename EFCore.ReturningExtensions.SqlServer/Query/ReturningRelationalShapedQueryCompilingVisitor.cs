using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using static System.Linq.Expressions.Expression;

namespace EFCore.ReturningExtensions.SqlServer.Query;

public class ReturningRelationalShapedQueryCompilingVisitorFactory(
    ShapedQueryCompilingExpressionVisitorDependencies dependencies,
    RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies)
    : IShapedQueryCompilingExpressionVisitorFactory
{
    protected virtual ShapedQueryCompilingExpressionVisitorDependencies Dependencies { get; } = dependencies;

    protected virtual RelationalShapedQueryCompilingExpressionVisitorDependencies RelationalDependencies { get; } = relationalDependencies;

    [DebuggerStepThrough]
    public virtual ShapedQueryCompilingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new ReturningRelationalShapedQueryCompilingVisitor(
            Dependencies,
            RelationalDependencies,
            queryCompilationContext);
}


public partial class ReturningRelationalShapedQueryCompilingVisitor : RelationalShapedQueryCompilingExpressionVisitor
{
    private readonly Type _contextType;
    private readonly ISet<string> _tags;
    private readonly bool _threadSafetyChecksEnabled;
    private readonly bool _detailedErrorsEnabled;
    

    /// <summary>
    ///     Creates a new instance of the <see cref="ShapedQueryCompilingExpressionVisitor" /> class.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this class.</param>
    /// <param name="relationalDependencies">Parameter object containing relational dependencies for this class.</param>
    /// <param name="queryCompilationContext">The query compilation context object to use.</param>
    public ReturningRelationalShapedQueryCompilingVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _contextType = queryCompilationContext.ContextType;
        _tags = queryCompilationContext.Tags;
        _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
        _detailedErrorsEnabled = dependencies.CoreSingletonOptions.AreDetailedErrorsEnabled;
    }

    [Experimental("EF9100")]
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
                    shapedQueryExpression.ShaperExpression,
                    out _,
                    out var readerColumns,
                    out _,
                    ref collectionCount);

            var readerColumnsExpression = CreateReaderColumnsExpression(readerColumns, Dependencies.LiftableConstantFactory);

            var relationalCommandResolver =
                CreateRelationalCommandResolverExpression(shapedQueryExpression.QueryExpression);

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

    [Experimental("EF9100")]
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

    [Experimental("EF9100")]
    public override void AddStructuralTypeInitialization(
        StructuralTypeShaperExpression shaper,
        ParameterExpression instanceVariable,
        List<ParameterExpression> variables,
        List<Expression> expressions)
    {
        var currentShaperProcessor = (ShaperProcessingExpressionVisitor)typeof(ReturningRelationalShapedQueryCompilingVisitor).BaseType!.GetField("_currentShaperProcessor",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!
            .GetValue(this)!;

        ProcessMi.Invoke(currentShaperProcessor, [shaper, instanceVariable, expressions]);

        // _currentShaperProcessor!.ProcessTopLevelComplexJsonProperties(shaper, instanceVariable, expressions);
    }

    private static readonly MethodInfo ProcessMi = typeof(ShaperProcessingExpressionVisitor).GetMethod(nameof(ShaperProcessingExpressionVisitor2.ProcessTopLevelComplexJsonProperties),
        BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo Mi = typeof(ReturningRelationalShapedQueryCompilingVisitor).BaseType!.GetMethod(nameof(CreateRelationalCommandResolverExpression),
        BindingFlags.NonPublic | BindingFlags.Instance)!;

    private Expression CreateRelationalCommandResolverExpression(Expression queryExpression)
    {
        return (Expression)Mi.Invoke(this, [queryExpression])!;
    }
}
