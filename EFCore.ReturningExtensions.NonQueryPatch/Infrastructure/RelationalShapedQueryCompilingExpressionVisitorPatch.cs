using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ReturningExtensions.NonQueryPatch.Infrastructure;

public class RelationalShapedQueryCompilingExpressionVisitorFactoryPatch : RelationalShapedQueryCompilingExpressionVisitorFactory
{
    public RelationalShapedQueryCompilingExpressionVisitorFactoryPatch(ShapedQueryCompilingExpressionVisitorDependencies dependencies, RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies) : base(dependencies, relationalDependencies)
    {
    }

    public override ShapedQueryCompilingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
    {
        return new RelationalShapedQueryCompilingExpressionVisitorPatch(
            Dependencies,
            RelationalDependencies,
            queryCompilationContext);
    }
}

public class RelationalShapedQueryCompilingExpressionVisitorPatch : RelationalShapedQueryCompilingExpressionVisitor
{
    private readonly Type _contextType;
    private readonly ISet<string> _tags;
    private readonly bool _threadSafetyChecksEnabled;
    private readonly bool _useRelationalNulls;

    public RelationalShapedQueryCompilingExpressionVisitorPatch(ShapedQueryCompilingExpressionVisitorDependencies dependencies, RelationalShapedQueryCompilingExpressionVisitorDependencies relationalDependencies, QueryCompilationContext queryCompilationContext)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _contextType = queryCompilationContext.ContextType;
        _tags = queryCompilationContext.Tags;
        _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
        _useRelationalNulls = RelationalOptionsExtension.Extract(queryCompilationContext.ContextOptions).UseRelationalNulls;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        return extensionExpression is NonQueryExpression nonQueryExpression
            ? VisitNonQuery(nonQueryExpression)
            : base.VisitExtension(extensionExpression);
    }

    protected virtual Expression VisitNonQuery(NonQueryExpression nonQueryExpression)
    {
        // Apply tags
        var innerExpression = nonQueryExpression.Expression;
        var returning = false;
        var type = innerExpression.Type;
        switch (innerExpression)
        {
            case DeleteExpression deleteExpression:
                innerExpression = deleteExpression.ApplyTags(_tags);
                break;
            case UpdateExpression updateExpression:
                returning = updateExpression.Returning;
                type = updateExpression.Type;
                innerExpression = updateExpression.ApplyTags(_tags);
                break;
        }

        var relationalCommandCache = new RelationalCommandCachePatch(
            Dependencies.MemoryCache,
            RelationalDependencies.QuerySqlGeneratorFactory,
            RelationalDependencies.RelationalParameterBasedSqlProcessorFactory,
            innerExpression,
            _useRelationalNulls);

        return Expression.Call(
            QueryCompilationContext.IsAsync
                ? NonQueryAsyncMethodInfo
                : (returning ? NonQueryMethodInfo3.MakeGenericMethod(type) : NonQueryMethodInfo),
            Expression.Convert(QueryCompilationContext.QueryContextParameter, typeof(RelationalQueryContext)),
            Expression.Constant(relationalCommandCache),
            Expression.Constant(_contextType),
            Expression.Constant(nonQueryExpression.CommandSource),
            Expression.Constant(_threadSafetyChecksEnabled));
    }

    private static readonly MethodInfo NonQueryMethodInfo3
        = typeof(RelationalShapedQueryCompilingExpressionVisitorPatch).GetTypeInfo()
            .GetDeclaredMethods(nameof(NonQueryResult3))
            .Single(mi => mi.GetParameters().Length == 5);

    private static readonly MethodInfo NonQueryMethodInfo
        = typeof(RelationalShapedQueryCompilingExpressionVisitorPatch).GetTypeInfo()
            .GetDeclaredMethods(nameof(NonQueryResult))
            .Single(mi => mi.GetParameters().Length == 5);

    private static readonly MethodInfo NonQueryAsyncMethodInfo
        = typeof(RelationalShapedQueryCompilingExpressionVisitorPatch).GetTypeInfo()
            .GetDeclaredMethods(nameof(NonQueryResultAsync))
            .Single(mi => mi.GetParameters().Length == 5);

    private static int NonQueryResult(
        RelationalQueryContext relationalQueryContext,
        RelationalCommandCachePatch relationalCommandCache,
        Type contextType,
        CommandSource commandSource,
        bool threadSafetyChecksEnabled)
    {
        if (threadSafetyChecksEnabled)
        {
            relationalQueryContext.ConcurrencyDetector.EnterCriticalSection();
        }

        try
        {
            return relationalQueryContext.ExecutionStrategy.Execute(
                (relationalQueryContext, relationalCommandCache, commandSource),
                static (_, state) =>
                {
                    EntityFrameworkEventSource.Log.QueryExecuting();

                    var relationalCommand =
                        state.relationalCommandCache.RentAndPopulateRelationalCommand(state.relationalQueryContext);

                    return relationalCommand.ExecuteNonQuery(
                        new RelationalCommandParameterObject(
                            state.relationalQueryContext.Connection,
                            state.relationalQueryContext.ParameterValues,
                            null,
                            state.relationalQueryContext.Context,
                            state.relationalQueryContext.CommandLogger,
                            state.commandSource));
                },
                null);
        }
        finally
        {
            if (threadSafetyChecksEnabled)
            {
                relationalQueryContext.ConcurrencyDetector.ExitCriticalSection();
            }
        }
    }

    private static IEnumerable<T> NonQueryResult3<T>(
        RelationalQueryContext relationalQueryContext,
        RelationalCommandCachePatch relationalCommandCache,
        Type contextType,
        CommandSource commandSource,
        bool threadSafetyChecksEnabled)
    {
        try
        {
            if (threadSafetyChecksEnabled)
            {
                relationalQueryContext.ConcurrencyDetector.EnterCriticalSection();
            }

            try
            {
                return relationalQueryContext.ExecutionStrategy.Execute(
                    (relationalQueryContext, relationalCommandCache, commandSource),
                    static (_, state) =>
                    {
                        EntityFrameworkEventSource.Log.QueryExecuting();

                        var relationalCommand = state.relationalCommandCache.RentAndPopulateRelationalCommand(state.relationalQueryContext);

                        using var reader = relationalCommand.ExecuteReader(
                            new RelationalCommandParameterObject(
                                state.relationalQueryContext.Connection,
                                state.relationalQueryContext.ParameterValues,
                                null,
                                state.relationalQueryContext.Context,
                                state.relationalQueryContext.CommandLogger,
                                state.commandSource));

                        var result = new List<T>();

                        var props = typeof(T)
                            .GetProperties()
                            .Where(x => x.CanWrite)
                            .OrderBy(x => x.Name)
                            .ToArray();

                        var ctor = typeof(T).GetConstructor(Array.Empty<Type>());

                        while (reader.DbDataReader.HasRows && reader.Read())
                        {
                            result.Add((T)ctor!.Invoke(Array.Empty<object>()));

                            for (int i = 0; i < reader.DbDataReader.FieldCount; i++)
                            {
                                props[i].SetValue(result[^1], reader.DbDataReader[i]);
                            }
                        }

                        return result;
                    },
                    null);

            }
            finally
            {
                if (threadSafetyChecksEnabled)
                {
                    relationalQueryContext.ConcurrencyDetector.ExitCriticalSection();
                }
            }
        }
        catch (Exception exception)
        {
            throw;
        }
    }

    private static Task<int> NonQueryResultAsync(
        RelationalQueryContext relationalQueryContext,
        RelationalCommandCachePatch relationalCommandCache,
        Type contextType,
        CommandSource commandSource,
        bool threadSafetyChecksEnabled)
    {
        if (threadSafetyChecksEnabled)
        {
            relationalQueryContext.ConcurrencyDetector.EnterCriticalSection();
        }

        try
        {
            return relationalQueryContext.ExecutionStrategy.ExecuteAsync(
                (relationalQueryContext, relationalCommandCache, commandSource),
                static (_, state, cancellationToken) =>
                {
                    EntityFrameworkEventSource.Log.QueryExecuting();

                    var relationalCommand = state.relationalCommandCache.RentAndPopulateRelationalCommand(state.relationalQueryContext);

                    return relationalCommand.ExecuteNonQueryAsync(
                        new RelationalCommandParameterObject(
                            state.relationalQueryContext.Connection,
                            state.relationalQueryContext.ParameterValues,
                            null,
                            state.relationalQueryContext.Context,
                            state.relationalQueryContext.CommandLogger,
                            state.commandSource),
                        cancellationToken);
                },
                null,
                relationalQueryContext.CancellationToken);
        }
        finally
        {
            if (threadSafetyChecksEnabled)
            {
                relationalQueryContext.ConcurrencyDetector.ExitCriticalSection();
            }
        }
    }

    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        if (shapedQueryExpression.QueryExpression is SelectExpression selectExpression)
        {
            VerifyNoClientConstant(shapedQueryExpression.ShaperExpression);
            var querySplittingBehavior =
                ((RelationalQueryCompilationContext)QueryCompilationContext).QuerySplittingBehavior;
            var splitQuery = querySplittingBehavior == QuerySplittingBehavior.SplitQuery;
            var collectionCount = 0;

            {
                var nonComposedFromSql = selectExpression.IsNonComposedFromSql();
                var shaper =
                    new ShaperProcessingExpressionVisitor(this, selectExpression, _tags, splitQuery, nonComposedFromSql)
                        .ProcessShaper(
                            shapedQueryExpression.ShaperExpression,
                            out var relationalCommandCache, out var readerColumns, out var relatedDataLoaders,
                            ref collectionCount);

                if (querySplittingBehavior == null
                    && collectionCount > 1)
                {
                    QueryCompilationContext.Logger.MultipleCollectionIncludeWarning();
                }

                return Expression.New(
                    typeof(SingleQueryingEnumerable<>).MakeGenericType(shaper.ReturnType).GetConstructors()[0],
                    Expression.Convert(QueryCompilationContext.QueryContextParameter, typeof(RelationalQueryContext)),
                    Expression.Constant(relationalCommandCache),
                    //Expression.Constant(readerColumns, typeof(IReadOnlyList<ReaderColumn?>)),
                    Expression.Constant(shaper.Compile()),
                    Expression.Constant(_contextType),
                    Expression.Constant(
                        QueryCompilationContext.QueryTrackingBehavior ==
                        QueryTrackingBehavior.NoTrackingWithIdentityResolution),
                    Expression.Constant(false),
                    Expression.Constant(_threadSafetyChecksEnabled));
            }
        }

        if (shapedQueryExpression.QueryExpression is UpdateExpression updateExpression)
        {
            VerifyNoClientConstant(shapedQueryExpression.ShaperExpression);
            var querySplittingBehavior =
                ((RelationalQueryCompilationContext)QueryCompilationContext).QuerySplittingBehavior;
            var splitQuery = querySplittingBehavior == QuerySplittingBehavior.SplitQuery;
            var collectionCount = 0;

            {
                var shaper =
                    new ShaperProcessingExpressionVisitor(this, updateExpression.SelectExpression, _tags, splitQuery, true)
                        .ProcessShaper(
                            shapedQueryExpression.ShaperExpression,
                            out var relationalCommandCache, out var readerColumns, out var relatedDataLoaders,
                            ref collectionCount);

                if (querySplittingBehavior == null
                    && collectionCount > 1)
                {
                    QueryCompilationContext.Logger.MultipleCollectionIncludeWarning();
                }

                return Expression.New(
                    typeof(SingleQueryingEnumerable<>).MakeGenericType(shaper.ReturnType).GetConstructors()[0],
                    Expression.Convert(QueryCompilationContext.QueryContextParameter, typeof(RelationalQueryContext)),
                    Expression.Constant(relationalCommandCache),
                    //Expression.Constant(readerColumns, typeof(IReadOnlyList<ReaderColumn?>)),
                    Expression.Constant(shaper.Compile()),
                    Expression.Constant(_contextType),
                    Expression.Constant(
                        QueryCompilationContext.QueryTrackingBehavior ==
                        QueryTrackingBehavior.NoTrackingWithIdentityResolution),
                    Expression.Constant(false),
                    Expression.Constant(_threadSafetyChecksEnabled));
            }
        }

        throw new NotImplementedException(shapedQueryExpression.ShaperExpression.GetType().Name);
    }

    private sealed class ShaperProcessingExpressionVisitor : ExpressionVisitor
    {
        private static readonly MethodInfo InitializeCollectionMethodInfo
            = typeof(ShaperProcessingExpressionVisitor).GetTypeInfo().GetDeclaredMethod(nameof(InitializeCollection))!;

        private static readonly MethodInfo PopulateCollectionMethodInfo
            = typeof(ShaperProcessingExpressionVisitor).GetTypeInfo().GetDeclaredMethod(nameof(PopulateCollection))!;

        private static readonly MethodInfo TaskAwaiterMethodInfo
            = typeof(ShaperProcessingExpressionVisitor).GetTypeInfo().GetDeclaredMethod(nameof(TaskAwaiter))!;


        // Reading database values
        private static readonly MethodInfo IsDbNullMethod =
            typeof(DbDataReader).GetRuntimeMethod(nameof(DbDataReader.IsDBNull), new[] { typeof(int) })!;

        public static readonly MethodInfo GetFieldValueMethod =
            typeof(DbDataReader).GetRuntimeMethod(nameof(DbDataReader.GetFieldValue), new[] { typeof(int) })!;

        // Coordinating results
        private static readonly MemberInfo ResultContextValuesMemberInfo
            = typeof(ResultContext).GetMember(nameof(ResultContext.Values))[0];

        private static readonly MemberInfo SingleQueryResultCoordinatorResultReadyMemberInfo
            = typeof(SingleQueryResultCoordinator).GetMember(nameof(SingleQueryResultCoordinator.ResultReady))[0];

        private static readonly MethodInfo CollectionAccessorAddMethodInfo
            = typeof(IClrCollectionAccessor).GetTypeInfo().GetDeclaredMethod(nameof(IClrCollectionAccessor.Add))!;

        private static readonly MethodInfo JsonElementGetPropertyMethod
            = typeof(JsonElement).GetMethod(nameof(JsonElement.GetProperty), new[] { typeof(string) })!;

        private static readonly MethodInfo JsonElementTryGetPropertyMethod
            = typeof(JsonElement).GetMethod(nameof(JsonElement.TryGetProperty), new[] { typeof(string), typeof(JsonElement).MakeByRefType() })!;

        private static readonly PropertyInfo _objectArrayIndexerPropertyInfo
            = typeof(object[]).GetProperty("Item")!;

        private static readonly PropertyInfo _nullableJsonElementHasValuePropertyInfo
            = typeof(JsonElement?).GetProperty(nameof(Nullable<JsonElement>.HasValue))!;

        private static readonly PropertyInfo _nullableJsonElementValuePropertyInfo
            = typeof(JsonElement?).GetProperty(nameof(Nullable<JsonElement>.Value))!;

        private readonly RelationalShapedQueryCompilingExpressionVisitorPatch _parentVisitor;
        private readonly ISet<string>? _tags;
        private readonly bool _isTracking;
        private readonly bool _isAsync;
        private readonly bool _splitQuery;
        private readonly bool _detailedErrorsEnabled;
        private readonly bool _generateCommandCache;
        private readonly ParameterExpression _resultCoordinatorParameter;
        private readonly ParameterExpression? _executionStrategyParameter;

        // States scoped to SelectExpression
        private readonly SelectExpression _selectExpression;
        private readonly ParameterExpression _dataReaderParameter;
        private readonly ParameterExpression _resultContextParameter;
        private readonly ParameterExpression? _indexMapParameter;
        private readonly ReaderColumn?[]? _readerColumns;

        // States to materialize only once
        private readonly Dictionary<Expression, Expression> _variableShaperMapping = new(ReferenceEqualityComparer.Instance);

        // There are always entity variables to avoid materializing same entity twice
        private readonly List<ParameterExpression> _variables = new();

        private readonly List<Expression> _expressions = new();

        // IncludeExpressions are added at the end in case they are using ValuesArray
        private readonly List<Expression> _includeExpressions = new();

        // If there is collection shaper then we need to construct ValuesArray to store values temporarily in ResultContext
        private List<Expression>? _collectionPopulatingExpressions;
        private Expression? _valuesArrayExpression;
        private List<Expression>? _valuesArrayInitializers;

        private bool _containsCollectionMaterialization;

        // Since identifiers for collection are not part of larger lambda they don't cannot use caching to materialize only once.
        private bool _inline;
        private int _collectionId;

        // States to convert code to data reader read
        private readonly Dictionary<ParameterExpression, IDictionary<IProperty, int>> _materializationContextBindings = new();
        private readonly Dictionary<ParameterExpression, object> _entityTypeIdentifyingExpressionInfo = new();
        private readonly Dictionary<ProjectionBindingExpression, string> _singleEntityTypeDiscriminatorValues = new();

        private readonly Dictionary<ParameterExpression, (ParameterExpression, ParameterExpression)> _jsonValueBufferParameterMapping =
            new();

        private readonly Dictionary<ParameterExpression, (ParameterExpression, ParameterExpression)>
            _jsonMaterializationContextParameterMapping = new();

        private readonly Dictionary<(int, string[]), ParameterExpression> _existingJsonElementMap
            = new(new ExistingJsonElementMapKeyComparer());

        public ShaperProcessingExpressionVisitor(
            RelationalShapedQueryCompilingExpressionVisitorPatch parentVisitor,
            SelectExpression selectExpression,
            ISet<string> tags,
            bool splitQuery,
            bool indexMap)
        {
            _parentVisitor = parentVisitor;
            _resultCoordinatorParameter = Expression.Parameter(
                splitQuery ? typeof(SplitQueryResultCoordinator) : typeof(SingleQueryResultCoordinator), "resultCoordinator");
            _executionStrategyParameter = splitQuery ? Expression.Parameter(typeof(IExecutionStrategy), "executionStrategy") : null;

            _selectExpression = selectExpression;
            _tags = tags;
            _dataReaderParameter = Expression.Parameter(typeof(DbDataReader), "dataReader");
            _resultContextParameter = Expression.Parameter(typeof(ResultContext), "resultContext");
            _indexMapParameter = indexMap ? Expression.Parameter(typeof(int[]), "indexMap") : null;
            if (parentVisitor.QueryCompilationContext.IsBuffering)
            {
                _readerColumns = new ReaderColumn?[_selectExpression.Projection.Count];
            }

            _generateCommandCache = true;
            _isTracking = parentVisitor.QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.TrackAll;
            _isAsync = parentVisitor.QueryCompilationContext.IsAsync;
            _splitQuery = splitQuery;

            _selectExpression.ApplyTags(_tags);
        }

        // For single query scenario
        private ShaperProcessingExpressionVisitor(
            RelationalShapedQueryCompilingExpressionVisitorPatch parentVisitor,
            ParameterExpression resultCoordinatorParameter,
            SelectExpression selectExpression,
            ParameterExpression dataReaderParameter,
            ParameterExpression resultContextParameter,
            ReaderColumn?[]? readerColumns)
        {
            _parentVisitor = parentVisitor;
            _resultCoordinatorParameter = resultCoordinatorParameter;

            _selectExpression = selectExpression;
            _dataReaderParameter = dataReaderParameter;
            _resultContextParameter = resultContextParameter;
            _readerColumns = readerColumns;
            _generateCommandCache = false;
            _isTracking = parentVisitor.QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.TrackAll;
            _isAsync = parentVisitor.QueryCompilationContext.IsAsync;
            _splitQuery = false;
        }

        // For split query scenario
        private ShaperProcessingExpressionVisitor(
            RelationalShapedQueryCompilingExpressionVisitorPatch parentVisitor,
            ParameterExpression resultCoordinatorParameter,
            ParameterExpression executionStrategyParameter,
            SelectExpression selectExpression,
            ISet<string> tags)
        {
            _parentVisitor = parentVisitor;
            _resultCoordinatorParameter = resultCoordinatorParameter;
            _executionStrategyParameter = executionStrategyParameter;

            _selectExpression = selectExpression;
            _tags = tags;
            _dataReaderParameter = Expression.Parameter(typeof(DbDataReader), "dataReader");
            _resultContextParameter = Expression.Parameter(typeof(ResultContext), "resultContext");
            if (parentVisitor.QueryCompilationContext.IsBuffering)
            {
                _readerColumns = new ReaderColumn[_selectExpression.Projection.Count];
            }

            _generateCommandCache = true;
            _isTracking = parentVisitor.QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.TrackAll;
            _isAsync = parentVisitor.QueryCompilationContext.IsAsync;
            _splitQuery = true;

            _selectExpression.ApplyTags(_tags);
        }

        private static TCollection InitializeCollection<TElement, TCollection>(
            int collectionId,
            QueryContext queryContext,
            DbDataReader dbDataReader,
            SingleQueryResultCoordinator resultCoordinator,
            Func<QueryContext, DbDataReader, object[]> parentIdentifier,
            Func<QueryContext, DbDataReader, object[]> outerIdentifier,
            IClrCollectionAccessor? clrCollectionAccessor)
            where TCollection : class, ICollection<TElement>
        {
            var collection = clrCollectionAccessor?.Create() ?? new List<TElement>();

            var parentKey = parentIdentifier(queryContext, dbDataReader);
            var outerKey = outerIdentifier(queryContext, dbDataReader);

            var collectionMaterializationContext = new SingleQueryCollectionContext(null, collection, parentKey, outerKey);

            resultCoordinator.SetSingleQueryCollectionContext(collectionId, collectionMaterializationContext);

            return (TCollection)collection;
        }

        private static void PopulateCollection<TCollection, TElement, TRelatedEntity>(
            int collectionId,
            QueryContext queryContext,
            DbDataReader dbDataReader,
            SingleQueryResultCoordinator resultCoordinator,
            Func<QueryContext, DbDataReader, object[]> parentIdentifier,
            Func<QueryContext, DbDataReader, object[]> outerIdentifier,
            Func<QueryContext, DbDataReader, object[]> selfIdentifier,
            IReadOnlyList<ValueComparer> parentIdentifierValueComparers,
            IReadOnlyList<ValueComparer> outerIdentifierValueComparers,
            IReadOnlyList<ValueComparer> selfIdentifierValueComparers,
            Func<QueryContext, DbDataReader, ResultContext, SingleQueryResultCoordinator, TRelatedEntity> innerShaper)
            where TRelatedEntity : TElement
            where TCollection : class, ICollection<TElement>
        {
            var collectionMaterializationContext = resultCoordinator.Collections[collectionId]!;
            if (collectionMaterializationContext.Collection is null)
            {
                // nothing to materialize since no collection created
                return;
            }

            if (resultCoordinator.HasNext == false)
            {
                // Outer Enumerator has ended
                GenerateCurrentElementIfPending();
                return;
            }

            if (!CompareIdentifiers(
                    outerIdentifierValueComparers,
                    outerIdentifier(queryContext, dbDataReader), collectionMaterializationContext.OuterIdentifier))
            {
                // Outer changed so collection has ended. Materialize last element.
                GenerateCurrentElementIfPending();
                // If parent also changed then this row is now pointing to element of next collection
                if (!CompareIdentifiers(
                        parentIdentifierValueComparers,
                        parentIdentifier(queryContext, dbDataReader), collectionMaterializationContext.ParentIdentifier))
                {
                    resultCoordinator.HasNext = true;
                }

                return;
            }

            var innerKey = selfIdentifier(queryContext, dbDataReader);
            if (innerKey.Length > 0 && innerKey.All(e => e == null))
            {
                // No correlated element
                return;
            }

            if (collectionMaterializationContext.SelfIdentifier != null)
            {
                if (CompareIdentifiers(
                        selfIdentifierValueComparers,
                        innerKey, collectionMaterializationContext.SelfIdentifier))
                {
                    // repeated row for current element
                    // If it is pending materialization then it may have nested elements
                    if (collectionMaterializationContext.ResultContext.Values != null)
                    {
                        ProcessCurrentElementRow();
                    }

                    resultCoordinator.ResultReady = false;
                    return;
                }

                // Row for new element which is not first element
                // So materialize the element
                GenerateCurrentElementIfPending();
                resultCoordinator.HasNext = null;
                collectionMaterializationContext.UpdateSelfIdentifier(innerKey);
            }
            else
            {
                // First row for current element
                collectionMaterializationContext.UpdateSelfIdentifier(innerKey);
            }

            ProcessCurrentElementRow();
            resultCoordinator.ResultReady = false;

            void ProcessCurrentElementRow()
            {
                var previousResultReady = resultCoordinator.ResultReady;
                resultCoordinator.ResultReady = true;
                var element = innerShaper(
                    queryContext, dbDataReader, collectionMaterializationContext.ResultContext, resultCoordinator);
                if (resultCoordinator.ResultReady)
                {
                    // related element is materialized
                    collectionMaterializationContext.ResultContext.Values = null;
                    ((TCollection)collectionMaterializationContext.Collection).Add(element);
                }

                resultCoordinator.ResultReady &= previousResultReady;
            }

            void GenerateCurrentElementIfPending()
            {
                if (collectionMaterializationContext.ResultContext.Values != null)
                {
                    resultCoordinator.HasNext = false;
                    ProcessCurrentElementRow();
                }

                collectionMaterializationContext.UpdateSelfIdentifier(null);
            }
        }


        private static bool CompareIdentifiers(IReadOnlyList<ValueComparer> valueComparers, object[] left, object[] right)
        {
            // Ignoring size check on all for perf as they should be same unless bug in code.
            for (var i = 0; i < left.Length; i++)
            {
                if (!valueComparers[i].Equals(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public LambdaExpression ProcessShaper(
            Expression shaperExpression,
            out RelationalCommandCache? relationalCommandCache,
            out IReadOnlyList<ReaderColumn?>? readerColumns,
            out LambdaExpression? relatedDataLoaders,
            ref int collectionId)
        {
            relatedDataLoaders = null;
            _collectionId = collectionId;

            if (_indexMapParameter != null)
            {
                var result = Visit(shaperExpression);
                _expressions.Add(result);
                result = Expression.Block(_variables, _expressions);

                relationalCommandCache = new RelationalCommandCache(
                    _parentVisitor.Dependencies.MemoryCache,
                    _parentVisitor.RelationalDependencies.QuerySqlGeneratorFactory,
                    _parentVisitor.RelationalDependencies.RelationalParameterBasedSqlProcessorFactory,
                    _selectExpression,
#if NET7_0
#else
                    _readerColumns,
#endif
                    _parentVisitor._useRelationalNulls);
                readerColumns = _readerColumns;

                return Expression.Lambda(
                    result,
                    QueryCompilationContext.QueryContextParameter,
                    _dataReaderParameter,
                    _indexMapParameter);
            }

            _containsCollectionMaterialization = new CollectionShaperFindingExpressionVisitor()
                .ContainsCollectionMaterialization(shaperExpression);

            if (!_containsCollectionMaterialization)
            {
                var result = Visit(shaperExpression);
                _expressions.AddRange(_includeExpressions);
                _expressions.Add(result);
                result = Expression.Block(_variables, _expressions);

                relationalCommandCache = _generateCommandCache
                    ? new RelationalCommandCache(
                        _parentVisitor.Dependencies.MemoryCache,
                        _parentVisitor.RelationalDependencies.QuerySqlGeneratorFactory,
                        _parentVisitor.RelationalDependencies.RelationalParameterBasedSqlProcessorFactory,
                        _selectExpression,
#if NET7_0
#else
                        _readerColumns,
#endif
                        _parentVisitor._useRelationalNulls)
                    : null;
                readerColumns = _readerColumns;

                return Expression.Lambda(
                    result,
                    QueryCompilationContext.QueryContextParameter,
                    _dataReaderParameter,
                    _resultContextParameter,
                    _resultCoordinatorParameter);
            }
            else
            {
                _valuesArrayExpression = Expression.MakeMemberAccess(_resultContextParameter, ResultContextValuesMemberInfo);
                _collectionPopulatingExpressions = new List<Expression>();
                _valuesArrayInitializers = new List<Expression>();

                var result = Visit(shaperExpression);

                var valueArrayInitializationExpression = Expression.Assign(
                    _valuesArrayExpression, Expression.NewArrayInit(typeof(object), _valuesArrayInitializers));

                _expressions.Add(valueArrayInitializationExpression);
                _expressions.AddRange(_includeExpressions);

                if (_splitQuery)
                {
                    _expressions.Add(Expression.Default(result.Type));

                    var initializationBlock = Expression.Block(_variables, _expressions);
                    result = Expression.Condition(
                        Expression.Equal(_valuesArrayExpression, Expression.Constant(null, typeof(object[]))),
                        initializationBlock,
                        result);

                    if (_isAsync)
                    {
                        var tasks = Expression.NewArrayInit(
                            typeof(Func<Task>), _collectionPopulatingExpressions.Select(
                                e => Expression.Lambda<Func<Task>>(e)));
                        relatedDataLoaders =
                            Expression.Lambda<Func<QueryContext, IExecutionStrategy, SplitQueryResultCoordinator, Task>>(
                                Expression.Call(TaskAwaiterMethodInfo, tasks),
                                QueryCompilationContext.QueryContextParameter,
                                _executionStrategyParameter!,
                                _resultCoordinatorParameter);
                    }
                    else
                    {
                        relatedDataLoaders =
                            Expression.Lambda<Action<QueryContext, IExecutionStrategy, SplitQueryResultCoordinator>>(
                                Expression.Block(_collectionPopulatingExpressions),
                                QueryCompilationContext.QueryContextParameter,
                                _executionStrategyParameter!,
                                _resultCoordinatorParameter);
                    }
                }
                else
                {
                    var initializationBlock = Expression.Block(_variables, _expressions);

                    var conditionalMaterializationExpressions = new List<Expression>
                    {
                        Expression.IfThen(
                            Expression.Equal(_valuesArrayExpression, Expression.Constant(null, typeof(object[]))),
                            initializationBlock)
                    };

                    conditionalMaterializationExpressions.AddRange(_collectionPopulatingExpressions);

                    conditionalMaterializationExpressions.Add(
                        Expression.Condition(
                            Expression.IsTrue(
                                Expression.MakeMemberAccess(
                                    _resultCoordinatorParameter, SingleQueryResultCoordinatorResultReadyMemberInfo)),
                            result,
                            Expression.Default(result.Type)));

                    result = Expression.Block(conditionalMaterializationExpressions);
                }

                relationalCommandCache = _generateCommandCache
                    ? new RelationalCommandCache(
                        _parentVisitor.Dependencies.MemoryCache,
                        _parentVisitor.RelationalDependencies.QuerySqlGeneratorFactory,
                        _parentVisitor.RelationalDependencies.RelationalParameterBasedSqlProcessorFactory,
                        _selectExpression,
#if NET7_0
#else
                        _readerColumns,
#endif
                        _parentVisitor._useRelationalNulls)
                    : null;
                readerColumns = _readerColumns;

                collectionId = _collectionId;

                return Expression.Lambda(
                    result,
                    QueryCompilationContext.QueryContextParameter,
                    _dataReaderParameter,
                    _resultContextParameter,
                    _resultCoordinatorParameter);
            }
        }

        protected override Expression VisitBinary(BinaryExpression binaryExpression)
        {
            if (binaryExpression.NodeType == ExpressionType.Assign
                && binaryExpression.Left is ParameterExpression parameterExpression
                && parameterExpression.Type == typeof(MaterializationContext))
            {
                var newExpression = (NewExpression)binaryExpression.Right;

                if (newExpression.Arguments[0] is ProjectionBindingExpression projectionBindingExpression)
                {
                    var propertyMap = (IDictionary<IProperty, int>)GetProjectionIndex(projectionBindingExpression);
                    _materializationContextBindings[parameterExpression] = propertyMap;
                    _entityTypeIdentifyingExpressionInfo[parameterExpression] =
                        // If single entity type is being selected in hierarchy then we use the value directly else we store the offset to
                        // read discriminator value.
                        _singleEntityTypeDiscriminatorValues.TryGetValue(projectionBindingExpression, out var value)
                            ? value
                            : propertyMap.Values.Max() + 1;

                    var updatedExpression = newExpression.Update(
                        new[] { Expression.Constant(ValueBuffer.Empty), newExpression.Arguments[1] });

                    return Expression.Assign(binaryExpression.Left, updatedExpression);
                }

                if (newExpression.Arguments[0] is ParameterExpression valueBufferParameter
                    && _jsonValueBufferParameterMapping.ContainsKey(valueBufferParameter))
                {
                    _jsonMaterializationContextParameterMapping[parameterExpression] =
                        _jsonValueBufferParameterMapping[valueBufferParameter];

                    var updatedExpression = newExpression.Update(
                        new[] { Expression.Constant(ValueBuffer.Empty), newExpression.Arguments[1] });

                    return Expression.Assign(binaryExpression.Left, updatedExpression);
                }
            }

            if (binaryExpression.NodeType == ExpressionType.Assign
                && binaryExpression.Left is MemberExpression memberExpression
                && memberExpression.Member is FieldInfo fieldInfo
                && fieldInfo.IsInitOnly)
            {
                return memberExpression.Assign(Visit(binaryExpression.Right));
            }

            return base.VisitBinary(binaryExpression);
        }

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            switch (extensionExpression)
            {
                case RelationalEntityShaperExpression entityShaperExpression
                    when !_inline && entityShaperExpression.ValueBufferExpression is ProjectionBindingExpression projectionBindingExpression:
                    {
                        if (!_variableShaperMapping.TryGetValue(entityShaperExpression.ValueBufferExpression, out var accessor))
                        {
                            {
                                var entityParameter = Expression.Parameter(entityShaperExpression.Type);
                                _variables.Add(entityParameter);
#if NET7_0_OR_GREATER
                                if (entityShaperExpression.EntityType.GetMappingStrategy() == RelationalAnnotationNames.TpcMappingStrategy)
                                {
                                    var concreteTypes = entityShaperExpression.EntityType.GetDerivedTypesInclusive().Where(e => !e.IsAbstract())
                                        .ToArray();
                                    // Single concrete TPC entity type won't have discriminator column.
                                    // We store the value here and inject it directly rather than reading from server.
                                    if (concreteTypes.Length == 1)
                                    {
                                        _singleEntityTypeDiscriminatorValues[
                                                (ProjectionBindingExpression)entityShaperExpression.ValueBufferExpression]
                                            = concreteTypes[0].ShortName();
                                    }
                                }
#endif

                                var entityMaterializationExpression = _parentVisitor.InjectEntityMaterializers(entityShaperExpression);
                                entityMaterializationExpression = Visit(entityMaterializationExpression);

                                _expressions.Add(Expression.Assign(entityParameter, entityMaterializationExpression));

                                {
                                    if (_containsCollectionMaterialization)
                                    {
                                        _valuesArrayInitializers!.Add(entityParameter);
                                        accessor = Expression.Convert(
                                            Expression.ArrayIndex(
                                                _valuesArrayExpression!,
                                                Expression.Constant(_valuesArrayInitializers.Count - 1)),
                                            entityShaperExpression.Type);
                                    }
                                    else
                                    {
                                        accessor = entityParameter;
                                    }
                                }
                            }

                            _variableShaperMapping[entityShaperExpression.ValueBufferExpression] = accessor;
                        }

                        return accessor;
                    }

                case RelationalEntityShaperExpression entityShaperExpression
                    when _inline && entityShaperExpression.ValueBufferExpression is ProjectionBindingExpression projectionBindingExpression:
                    {
#if NET7_0_OR_GREATER
                        if (entityShaperExpression.EntityType.GetMappingStrategy() == "RelationalAnnotationNames.TpcMappingStrategy")
                        {
                            var concreteTypes = entityShaperExpression.EntityType.GetDerivedTypesInclusive().Where(e => !e.IsAbstract())
                                .ToArray();
                            // Single concrete TPC entity type won't have discriminator column.
                            // We store the value here and inject it directly rather than reading from server.
                            if (concreteTypes.Length == 1)
                            {
                                _singleEntityTypeDiscriminatorValues[
                                        (ProjectionBindingExpression)entityShaperExpression.ValueBufferExpression]
                                    = concreteTypes[0].ShortName();
                            }
                        }
#endif

                        var entityMaterializationExpression = _parentVisitor.InjectEntityMaterializers(entityShaperExpression);
                        entityMaterializationExpression = Visit(entityMaterializationExpression);

                        return entityMaterializationExpression;
                    }

                case CollectionResultExpression collectionResultExpression
                    when collectionResultExpression.Navigation is INavigation navigation
                    && GetProjectionIndex(collectionResultExpression.ProjectionBindingExpression)
                        is ValueTuple<int, List<(IProperty, int)>, string[]> jsonProjectionIndex:
                    {
                        // json entity collection at the root
                        var (jsonElementParameter, keyValuesParameter) = JsonShapingPreProcess(
                            jsonProjectionIndex,
                            navigation.TargetEntityType,
                            isCollection: true);

                        var jsonCollectionParameter = Expression.Parameter(collectionResultExpression.Type);

                        _variables.Add(jsonCollectionParameter);

                        return CompensateForCollectionMaterialization(
                            jsonCollectionParameter,
                            collectionResultExpression.Type);

                    }

                case ProjectionBindingExpression projectionBindingExpression
                    when _inline:
                    {
                        var projectionIndex = (int)GetProjectionIndex(projectionBindingExpression);
                        var projection = _selectExpression.Projection[projectionIndex];

                        return CreateGetValueExpression(
                            _dataReaderParameter,
                            projectionIndex,
                            IsNullableProjection(projection),
                            projection.Expression.TypeMapping!,
                            projectionBindingExpression.Type);
                    }

                case ProjectionBindingExpression projectionBindingExpression
                    when !_inline:
                    {
                        if (_variableShaperMapping.TryGetValue(projectionBindingExpression, out var accessor))
                        {
                            return accessor;
                        }

                        var projectionIndex = (int)GetProjectionIndex(projectionBindingExpression);
                        var projection = _selectExpression.Projection[projectionIndex];
                        var nullable = IsNullableProjection(projection);

                        var valueParameter = Expression.Parameter(projectionBindingExpression.Type);
                        _variables.Add(valueParameter);

                        _expressions.Add(
                            Expression.Assign(
                                valueParameter,
                                CreateGetValueExpression(
                                    _dataReaderParameter,
                                    projectionIndex,
                                    nullable,
                                    projection.Expression.TypeMapping!,
                                    valueParameter.Type)));

                        if (_containsCollectionMaterialization)
                        {
                            var expressionToAdd = (Expression)valueParameter;
                            if (expressionToAdd.Type.IsValueType)
                            {
                                expressionToAdd = Expression.Convert(expressionToAdd, typeof(object));
                            }

                            _valuesArrayInitializers!.Add(expressionToAdd);
                            accessor = Expression.Convert(
                                Expression.ArrayIndex(
                                    _valuesArrayExpression!,
                                    Expression.Constant(_valuesArrayInitializers.Count - 1)),
                                projectionBindingExpression.Type);
                        }
                        else
                        {
                            accessor = valueParameter;
                        }

                        _variableShaperMapping[projectionBindingExpression] = accessor;

                        return accessor;
                    }

                case IncludeExpression includeExpression:
                    {
                        var entity = Visit(includeExpression.EntityExpression);
                        if (includeExpression.NavigationExpression is RelationalCollectionShaperExpression
                            relationalCollectionShaperExpression)
                        {
                            var collectionIdConstant = Expression.Constant(_collectionId++);
                            var innerShaper = new ShaperProcessingExpressionVisitor(
                                    _parentVisitor, _resultCoordinatorParameter, _selectExpression, _dataReaderParameter,
                                    _resultContextParameter,
                                    _readerColumns)
                                .ProcessShaper(relationalCollectionShaperExpression.InnerShaper, out _, out _, out _, ref _collectionId);

                            var entityType = entity.Type;
                            var navigation = includeExpression.Navigation;
                            var includingEntityType = navigation.DeclaringEntityType.ClrType;
                            if (includingEntityType != entityType
                                && includingEntityType.IsAssignableFrom(entityType))
                            {
                                includingEntityType = entityType;
                            }

                            _inline = true;

                            var parentIdentifierLambda = Expression.Lambda(
                                Visit(relationalCollectionShaperExpression.ParentIdentifier),
                                QueryCompilationContext.QueryContextParameter,
                                _dataReaderParameter);

                            var outerIdentifierLambda = Expression.Lambda(
                                Visit(relationalCollectionShaperExpression.OuterIdentifier),
                                QueryCompilationContext.QueryContextParameter,
                                _dataReaderParameter);

                            var selfIdentifierLambda = Expression.Lambda(
                                Visit(relationalCollectionShaperExpression.SelfIdentifier),
                                QueryCompilationContext.QueryContextParameter,
                                _dataReaderParameter);

                            _inline = false;

                            var relatedEntityType = innerShaper.ReturnType;
                            var inverseNavigation = navigation.Inverse;
                        }
                        else if (includeExpression.NavigationExpression is RelationalSplitCollectionShaperExpression
                                 relationalSplitCollectionShaperExpression)
                        {
                            var collectionIdConstant = Expression.Constant(_collectionId++);
                            var innerProcessor = new ShaperProcessingExpressionVisitor(
                                _parentVisitor, _resultCoordinatorParameter,
                                _executionStrategyParameter!, relationalSplitCollectionShaperExpression.SelectExpression, _tags!);
                            var innerShaper = innerProcessor.ProcessShaper(
                                relationalSplitCollectionShaperExpression.InnerShaper,
                                out var relationalCommandCache,
                                out var readerColumns,
                                out var relatedDataLoaders,
                                ref _collectionId);

                            var entityType = entity.Type;
                            var navigation = includeExpression.Navigation;
                            var includingEntityType = navigation.DeclaringEntityType.ClrType;
                            if (includingEntityType != entityType
                                && includingEntityType.IsAssignableFrom(entityType))
                            {
                                includingEntityType = entityType;
                            }

                            _inline = true;

                            var parentIdentifierLambda = Expression.Lambda(
                                Visit(relationalSplitCollectionShaperExpression.ParentIdentifier),
                                QueryCompilationContext.QueryContextParameter,
                                _dataReaderParameter);

                            _inline = false;

                            innerProcessor._inline = true;

                            var childIdentifierLambda = Expression.Lambda(
                                innerProcessor.Visit(relationalSplitCollectionShaperExpression.ChildIdentifier),
                                QueryCompilationContext.QueryContextParameter,
                                innerProcessor._dataReaderParameter);

                            innerProcessor._inline = false;

                            var relatedEntityType = innerShaper.ReturnType;
                            var inverseNavigation = navigation.Inverse;
                        }
                        else
                        {
                            var projectionBindingExpression = (includeExpression.NavigationExpression as CollectionResultExpression)
                                ?.ProjectionBindingExpression
                                ?? (includeExpression.NavigationExpression as RelationalEntityShaperExpression)?.ValueBufferExpression as
                                ProjectionBindingExpression;

                            // json include case
                            if (projectionBindingExpression != null
                                && GetProjectionIndex(projectionBindingExpression) is ValueTuple<int, List<(IProperty, int)>, string[]>
                                    jsonProjectionIndex)
                            {
                                var (jsonElementParameter, keyValuesParameter) = JsonShapingPreProcess(
                                    jsonProjectionIndex,
                                    includeExpression.Navigation.TargetEntityType,
                                    includeExpression.Navigation.IsCollection);

                                return entity;
                            }

                            var navigationExpression = Visit(includeExpression.NavigationExpression);
                            var entityType = entity.Type;
                            var navigation = includeExpression.Navigation;
                            var includingType = navigation.DeclaringEntityType.ClrType;
                            var inverseNavigation = navigation.Inverse;
                            var relatedEntityType = navigation.TargetEntityType.ClrType;
                            if (includingType != entityType
                                && includingType.IsAssignableFrom(entityType))
                            {
                                includingType = entityType;
                            }
                        }

                        return entity;
                    }

                case RelationalCollectionShaperExpression relationalCollectionShaperExpression:
                    {
                        if (!_variableShaperMapping.TryGetValue(relationalCollectionShaperExpression, out var accessor))
                        {
                            var collectionIdConstant = Expression.Constant(_collectionId++);
                            var innerShaper = new ShaperProcessingExpressionVisitor(
                                    _parentVisitor, _resultCoordinatorParameter, _selectExpression, _dataReaderParameter,
                                    _resultContextParameter,
                                    _readerColumns)
                                .ProcessShaper(relationalCollectionShaperExpression.InnerShaper, out _, out _, out _, ref _collectionId);

                            var navigation = relationalCollectionShaperExpression.Navigation;
                            var collectionAccessor = navigation?.GetCollectionAccessor();
                            var collectionType = collectionAccessor?.CollectionType ?? relationalCollectionShaperExpression.Type;
                            var elementType = relationalCollectionShaperExpression.ElementType;
                            var relatedElementType = innerShaper.ReturnType;

                            _inline = true;

                            var parentIdentifierLambda = Expression.Lambda(
                                Visit(relationalCollectionShaperExpression.ParentIdentifier),
                                QueryCompilationContext.QueryContextParameter,
                                _dataReaderParameter);

                            var outerIdentifierLambda = Expression.Lambda(
                                Visit(relationalCollectionShaperExpression.OuterIdentifier),
                                QueryCompilationContext.QueryContextParameter,
                                _dataReaderParameter);

                            var selfIdentifierLambda = Expression.Lambda(
                                Visit(relationalCollectionShaperExpression.SelfIdentifier),
                                QueryCompilationContext.QueryContextParameter,
                                _dataReaderParameter);

                            _inline = false;

                            var collectionParameter = Expression.Parameter(relationalCollectionShaperExpression.Type);
                            _variables.Add(collectionParameter);
                            _expressions.Add(
                                Expression.Assign(
                                    collectionParameter,
                                    Expression.Call(
                                        InitializeCollectionMethodInfo.MakeGenericMethod(elementType, collectionType),
                                        collectionIdConstant,
                                        QueryCompilationContext.QueryContextParameter,
                                        _dataReaderParameter,
                                        _resultCoordinatorParameter,
                                        Expression.Constant(parentIdentifierLambda.Compile()),
                                        Expression.Constant(outerIdentifierLambda.Compile()),
                                        Expression.Constant(collectionAccessor, typeof(IClrCollectionAccessor)))));

                            _valuesArrayInitializers!.Add(collectionParameter);
                            accessor = Expression.Convert(
                                Expression.ArrayIndex(
                                    _valuesArrayExpression!,
                                    Expression.Constant(_valuesArrayInitializers.Count - 1)),
                                relationalCollectionShaperExpression.Type);

                            _collectionPopulatingExpressions!.Add(
                                Expression.Call(
                                    PopulateCollectionMethodInfo.MakeGenericMethod(collectionType, elementType, relatedElementType),
                                    collectionIdConstant,
                                    QueryCompilationContext.QueryContextParameter,
                                    _dataReaderParameter,
                                    _resultCoordinatorParameter,
                                    Expression.Constant(parentIdentifierLambda.Compile()),
                                    Expression.Constant(outerIdentifierLambda.Compile()),
                                    Expression.Constant(selfIdentifierLambda.Compile()),
                                    Expression.Constant(
                                        relationalCollectionShaperExpression.ParentIdentifierValueComparers,
                                        typeof(IReadOnlyList<ValueComparer>)),
                                    Expression.Constant(
                                        relationalCollectionShaperExpression.OuterIdentifierValueComparers,
                                        typeof(IReadOnlyList<ValueComparer>)),
                                    Expression.Constant(
                                        relationalCollectionShaperExpression.SelfIdentifierValueComparers,
                                        typeof(IReadOnlyList<ValueComparer>)),
                                    Expression.Constant(innerShaper.Compile())));

                            _variableShaperMapping[relationalCollectionShaperExpression] = accessor;
                        }

                        return accessor;
                    }
            }

            return base.VisitExtension(extensionExpression);

            Expression CompensateForCollectionMaterialization(ParameterExpression parameter, Type resultType)
            {
                if (_containsCollectionMaterialization)
                {
                    _valuesArrayInitializers!.Add(parameter);
                    return Expression.Convert(
                        Expression.ArrayIndex(
                            _valuesArrayExpression!,
                            Expression.Constant(_valuesArrayInitializers.Count - 1)),
                        resultType);
                }
                else
                {
                    return parameter;
                }
            }
        }

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method.IsGenericMethod
                && methodCallExpression.Method.GetGenericMethodDefinition()
                == ExpressionExtensions.ValueBufferTryReadValueMethod)
            {
                var index = methodCallExpression.Arguments[1].GetConstantValue<int>();
                var property = methodCallExpression.Arguments[2].GetConstantValue<IProperty?>();
                var mappingParameter = (ParameterExpression)((MethodCallExpression)methodCallExpression.Arguments[0]).Object!;

                int projectionIndex;
                if (property == null)
                {
                    // This is trying to read the computed discriminator value
                    var storedInfo = _entityTypeIdentifyingExpressionInfo[mappingParameter];
                    if (storedInfo is string s)
                    {
                        // If the value is fixed then there is single entity type and discriminator is not present in query
                        // We just return the value as-is.
                        return Expression.Constant(s);
                    }

                    projectionIndex = (int)_entityTypeIdentifyingExpressionInfo[mappingParameter] + index;
                }
                else
                {
                    projectionIndex = _materializationContextBindings[mappingParameter][property];
                }

                var projection = _selectExpression.Projection[projectionIndex];
                var nullable = IsNullableProjection(projection);

                Debug.Assert(
                    !nullable || property != null || methodCallExpression.Type.IsNullableType(),
                    "For nullable reads the return type must be null unless property is specified.");

                return CreateGetValueExpression(
                    _dataReaderParameter,
                    projectionIndex,
                    nullable,
                    projection.Expression.TypeMapping!,
                    methodCallExpression.Type,
                    property);
            }

            return base.VisitMethodCall(methodCallExpression);
        }

        private (ParameterExpression, ParameterExpression) JsonShapingPreProcess(
            ValueTuple<int, List<(IProperty, int)>, string[]> projectionIndex,
            IEntityType entityType,
            bool isCollection)
        {
            var jsonColumnProjectionIndex = projectionIndex.Item1;
            var keyInfo = projectionIndex.Item2;
            var additionalPath = projectionIndex.Item3;

            var keyValuesParameter = Expression.Parameter(typeof(object[]));
            var keyValues = new Expression[keyInfo.Count];

            for (var i = 0; i < keyInfo.Count; i++)
            {
                var projection = _selectExpression.Projection[keyInfo[i].Item2];

                keyValues[i] = Expression.Convert(
                    CreateGetValueExpression(
                        _dataReaderParameter,
                        keyInfo[i].Item2,
                        IsNullableProjection(projection),
                        projection.Expression.TypeMapping!,
                        keyInfo[i].Item1.ClrType,
                        keyInfo[i].Item1),
                    typeof(object));
            }

            var keyValuesInitialize = Expression.NewArrayInit(typeof(object), keyValues);
            var keyValuesAssignment = Expression.Assign(keyValuesParameter, keyValuesInitialize);

            _variables.Add(keyValuesParameter);
            _expressions.Add(keyValuesAssignment);

            var jsonColumnTypeMapping = entityType.GetContainerColumnTypeMapping()!;
            if (_existingJsonElementMap.TryGetValue((jsonColumnProjectionIndex, additionalPath), out var exisitingJsonElementVariable))
            {
                return (exisitingJsonElementVariable, keyValuesParameter);
            }

            // TODO: this logic could/should be improved (later)
            var currentJsonElementVariable = default(ParameterExpression);
            var index = 0;
            do
            {
                // try to find JsonElement variable for this json column and path if we encountered (and cached it) before
                // otherwise either create new JsonElement from the data reader if we are at root level
                // or build on top of previous variable withing the navigation chain (e.g. when we encountered the root before, but not this entire path)
                if (!_existingJsonElementMap.TryGetValue(
                        (jsonColumnProjectionIndex, additionalPath[..index]), out var exisitingJsonElementVariable2))
                {
                    var jsonElementVariable = Expression.Variable(
                        typeof(JsonElement?));

                    {
                        Expression jsonElementValueExpression;
                        if (index == 0)
                        {
                            jsonElementValueExpression = CreateGetValueExpression(
                                _dataReaderParameter,
                                jsonColumnProjectionIndex,
                                nullable: true,
                                jsonColumnTypeMapping,
                                typeof(JsonElement?),
                                property: null);
                        }
                        else
                        {
                            var tempParameter = Expression.Variable(typeof(JsonElement));
                            _variables.Add(tempParameter);

                            var tryGetPropertyCall = Expression.Call(
                                Expression.MakeMemberAccess(
                                    currentJsonElementVariable!,
                                    _nullableJsonElementValuePropertyInfo),
                                JsonElementTryGetPropertyMethod,
                                Expression.Constant(additionalPath[index - 1]),
                                tempParameter);

                            jsonElementValueExpression = Expression.Condition(
                                Expression.AndAlso(
                                    Expression.MakeMemberAccess(
                                        currentJsonElementVariable!,
                                        _nullableJsonElementHasValuePropertyInfo),
                                    tryGetPropertyCall),
                                Expression.Convert(tempParameter, typeof(JsonElement?)),
                                Expression.Constant(null, typeof(JsonElement?)));
                        }

                        var jsonElementAssignment = Expression.Assign(
                            jsonElementVariable,
                            jsonElementValueExpression);

                        _variables.Add(jsonElementVariable);
                        _expressions.Add(jsonElementAssignment);
                        _existingJsonElementMap[(jsonColumnProjectionIndex, additionalPath[..index])] = jsonElementVariable;

                        currentJsonElementVariable = jsonElementVariable;
                    }
                }
                else
                {
                    currentJsonElementVariable = exisitingJsonElementVariable2;
                }

                index++;
            }
            while (index <= additionalPath.Length);

            return (currentJsonElementVariable!, keyValuesParameter);
        }

        private static LambdaExpression GenerateFixup(
            Type entityType,
            Type relatedEntityType,
            INavigationBase navigation,
            INavigationBase? inverseNavigation)
        {
            var entityParameter = Expression.Parameter(entityType);
            var relatedEntityParameter = Expression.Parameter(relatedEntityType);
            var expressions = new List<Expression>();

            if (!navigation.IsShadowProperty())
            {
                expressions.Add(
                    navigation.IsCollection
                        ? AddToCollectionNavigation(entityParameter, relatedEntityParameter, navigation)
                        : AssignReferenceNavigation(entityParameter, relatedEntityParameter, navigation));
            }

            if (inverseNavigation != null
                && !inverseNavigation.IsShadowProperty())
            {
                expressions.Add(
                    inverseNavigation.IsCollection
                        ? AddToCollectionNavigation(relatedEntityParameter, entityParameter, inverseNavigation)
                        : AssignReferenceNavigation(relatedEntityParameter, entityParameter, inverseNavigation));
            }

            return Expression.Lambda(Expression.Block(typeof(void), expressions), entityParameter, relatedEntityParameter);
        }

        private static Expression AssignReferenceNavigation(
            ParameterExpression entity,
            ParameterExpression relatedEntity,
            INavigationBase navigation)
            => entity.MakeMemberAccess(navigation.GetMemberInfo(forMaterialization: true, forSet: true)).Assign(relatedEntity);

        private static Expression AddToCollectionNavigation(
            ParameterExpression entity,
            ParameterExpression relatedEntity,
            INavigationBase navigation)
            => Expression.Call(
                Expression.Constant(navigation.GetCollectionAccessor()),
                CollectionAccessorAddMethodInfo,
                entity,
                relatedEntity,
                Expression.Constant(true));

        private object GetProjectionIndex(ProjectionBindingExpression projectionBindingExpression)
            => _selectExpression.GetProjection(projectionBindingExpression).GetConstantValue<object>();

        private static bool IsNullableProjection(ProjectionExpression projection)
            => projection.Expression is not ColumnExpression column || column.IsNullable;

        private Expression CreateGetValueExpression(
            ParameterExpression dbDataReader,
            int index,
            bool nullable,
            RelationalTypeMapping typeMapping,
            Type type,
            IPropertyBase? property = null)
        {
            Debug.Assert(
                property != null || type.IsNullableType(), "Must read nullable value from database if property is not specified.");

            var getMethod = typeMapping.GetDataReaderMethod();

            Expression indexExpression = Expression.Constant(index);
            if (_indexMapParameter != null)
            {
                indexExpression = Expression.ArrayIndex(_indexMapParameter, indexExpression);
            }

            Expression valueExpression
                = Expression.Call(
                    getMethod.DeclaringType != typeof(DbDataReader)
                        ? Expression.Convert(dbDataReader, getMethod.DeclaringType!)
                        : dbDataReader,
                    getMethod,
                    indexExpression);

            var buffering = false;

            if (_readerColumns != null)
            {
                buffering = true;
                var columnType = valueExpression.Type;
                var bufferedColumnType = columnType;
                if (!bufferedColumnType.IsValueType
                    || !BufferedDataReader.IsSupportedValueType(bufferedColumnType))
                {
                    bufferedColumnType = typeof(object);
                }

                if (_readerColumns[index] == null)
                {
                    var bufferedReaderLambdaExpression = valueExpression;
                    if (columnType != bufferedColumnType)
                    {
                        bufferedReaderLambdaExpression = Expression.Convert(bufferedReaderLambdaExpression, bufferedColumnType);
                    }

                    _readerColumns[index] = ReaderColumn.Create(
                        bufferedColumnType,
                        nullable,
                        _indexMapParameter != null ? ((ColumnExpression)_selectExpression.Projection[index].Expression).Name : null,
                        property,
                        Expression.Lambda(
                            bufferedReaderLambdaExpression,
                            dbDataReader,
                            _indexMapParameter ?? Expression.Parameter(typeof(int[]))).Compile());
                }

                valueExpression = Expression.Call(
                    dbDataReader, RelationalTypeMapping.GetDataReaderMethod(bufferedColumnType), indexExpression);
                if (valueExpression.Type != columnType)
                {
                    valueExpression = Expression.Convert(valueExpression, columnType);
                }
            }

            valueExpression = typeMapping.CustomizeDataReaderExpression(valueExpression);

            var converter = typeMapping.Converter;

            if (converter != null)
            {
                if (valueExpression.Type != converter.ProviderClrType)
                {
                    valueExpression = Expression.Convert(valueExpression, converter.ProviderClrType);
                }

                valueExpression = ReplacingExpressionVisitor.Replace(
                    converter.ConvertFromProviderExpression.Parameters.Single(),
                    valueExpression,
                    converter.ConvertFromProviderExpression.Body);
            }

            if (valueExpression.Type != type)
            {
                valueExpression = Expression.Convert(valueExpression, type);
            }

            if (nullable)
            {
                Expression replaceExpression;
                if (converter?.ConvertsNulls == true)
                {
                    replaceExpression = ReplacingExpressionVisitor.Replace(
                        converter.ConvertFromProviderExpression.Parameters.Single(),
                        Expression.Default(converter.ProviderClrType),
                        converter.ConvertFromProviderExpression.Body);

                    if (replaceExpression.Type != type)
                    {
                        replaceExpression = Expression.Convert(replaceExpression, type);
                    }
                }
                else
                {
                    replaceExpression = Expression.Default(valueExpression.Type);
                }

                valueExpression = Expression.Condition(
                    Expression.Call(dbDataReader, IsDbNullMethod, indexExpression),
                    replaceExpression,
                    valueExpression);
            }

            return valueExpression;
        }

        private sealed class CollectionShaperFindingExpressionVisitor : ExpressionVisitor
        {
            private bool _containsCollection;

            public bool ContainsCollectionMaterialization(Expression expression)
            {
                _containsCollection = false;

                Visit(expression);

                return _containsCollection;
            }

            [return: NotNullIfNotNull("expression")]
            public override Expression? Visit(Expression? expression)
            {
                if (_containsCollection)
                {
                    return expression;
                }

                if (expression is RelationalCollectionShaperExpression
                    || expression is RelationalSplitCollectionShaperExpression)
                {
                    _containsCollection = true;

                    return expression;
                }

                return base.Visit(expression);
            }
        }

        private sealed class ExistingJsonElementMapKeyComparer : IEqualityComparer<(int, string[])>
        {
            public bool Equals((int, string[]) x, (int, string[]) y)
                => x.Item1 == y.Item1 && x.Item2.Length == y.Item2.Length && x.Item2.SequenceEqual(y.Item2);

            public int GetHashCode([DisallowNull] (int, string[]) obj)
                => HashCode.Combine(obj.Item1, obj.Item2?.Length);
        }
    }
}