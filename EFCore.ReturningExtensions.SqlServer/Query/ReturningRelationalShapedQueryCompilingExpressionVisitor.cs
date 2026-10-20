// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Modified by Gordeev Dmitry in 2026 for EFCore.ReturningExtensions.
// Original source: Entity Framework Core (https://github.com/dotnet/efcore), licensed under the MIT License.

using EFCore.ReturningExtensions.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using static System.Linq.Expressions.Expression;

namespace EFCore.ReturningExtensions.SqlServer.Query;

public class ReturningRelationalShapedQueryCompilingExpressionVisitorFactory(
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
    private readonly IReadOnlySet<string> _parametersToConstantize;
    private readonly Type _contextType;
    private readonly ISet<string> _tags;
    private readonly bool _threadSafetyChecksEnabled;
    private readonly bool _detailedErrorsEnabled;
    private readonly bool _useRelationalNulls;
    private readonly bool _isPrecompiling;

    private readonly RelationalParameterBasedSqlProcessor _relationalParameterBasedSqlProcessor;
    private readonly IQuerySqlGeneratorFactory _querySqlGeneratorFactory;

    private static ConstructorInfo? _relationalCommandConstructor;
    private static ConstructorInfo? _typeMappedRelationalParameterConstructor;
    private static PropertyInfo? _commandBuilderDependenciesProperty;
    private static MethodInfo? _getRelationalCommandTemplateMethod;

    private static ConstructorInfo? _hashSetConstructor;
    private static PropertyInfo? _stringComparerOrdinalProperty;
    private static ConstructorInfo? _relationalCommandCacheConstructor;
    private static PropertyInfo? _dependenciesProperty;
    private static PropertyInfo? _dependenciesMemoryCacheProperty;
    private static PropertyInfo? _relationalDependenciesProperty;
    private static PropertyInfo? _relationalDependenciesQuerySqlGeneratorFactoryProperty;
    private static PropertyInfo? _relationalDependenciesRelationalParameterBasedSqlProcessorFactoryProperty;

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
        RelationalDependencies = relationalDependencies;

        _parametersToConstantize = (IReadOnlySet<string>)QueryCompilationContext.ParametersToConstantize;

        _relationalParameterBasedSqlProcessor =
            relationalDependencies.RelationalParameterBasedSqlProcessorFactory.Create(
                new RelationalParameterBasedSqlProcessorParameters(_useRelationalNulls, _parametersToConstantize));
        _querySqlGeneratorFactory = relationalDependencies.QuerySqlGeneratorFactory;

        _contextType = queryCompilationContext.ContextType;
        _tags = queryCompilationContext.Tags;
        _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
        _detailedErrorsEnabled = dependencies.CoreSingletonOptions.AreDetailedErrorsEnabled;
        _useRelationalNulls = RelationalOptionsExtension.Extract(queryCompilationContext.ContextOptions).UseRelationalNulls;
        _isPrecompiling = queryCompilationContext.IsPrecompiling;
    }

    /// <summary>
    ///     Relational provider-specific dependencies for this service.
    /// </summary>
    protected virtual RelationalShapedQueryCompilingExpressionVisitorDependencies RelationalDependencies { get; }

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
            var shaper = new ShaperProcessingExpressionVisitor2(this, selectExpression, _tags, splitQuery, nonComposedFromSql)
                .ProcessShaper(
                    shapedQueryExpression.ShaperExpression, out var relationalCommandResolver, out var readerColumns,
                    out var relatedDataLoaders, ref collectionCount);

            if (querySplittingBehavior == null
                && collectionCount > 1)
            {
                QueryCompilationContext.Logger.MultipleCollectionIncludeWarning();
            }

            var readerColumnsExpression = CreateReaderColumnsExpression(readerColumns, Dependencies.LiftableConstantFactory);

            relationalCommandResolver =
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

    private Expression CreateRelationalCommandResolverExpression(Expression queryExpression)
    {
        // In the regular case, we generate code that accesses the RelationalCommandCache (which invokes the 2nd part of the
        // query pipeline). This is only skipped in query precompilation with few nullable parameters, where we pregenerate the SQL,
        // bypassing the RelationalCommandCache (no more 2nd part of the query pipeline at runtime).
        if (_isPrecompiling && TryGeneratePregeneratedCommandResolver(queryExpression, out var relationalCommandResolver))
        {
            return relationalCommandResolver;
        }

        var relationalCommandCache = new RelationalCommandCache(
            Dependencies.MemoryCache,
            RelationalDependencies.QuerySqlGeneratorFactory,
            RelationalDependencies.RelationalParameterBasedSqlProcessorFactory,
            queryExpression,
            _useRelationalNulls,
            _parametersToConstantize);

        var commandLiftableConstant = RelationalDependencies.RelationalLiftableConstantFactory.CreateLiftableConstant(
            relationalCommandCache,
            GenerateRelationalCommandCacheExpression(),
            "relationalCommandCache",
            typeof(RelationalCommandCache));

        var parametersParameter = Parameter(typeof(IReadOnlyDictionary<string, object?>), "parameters");

        return Lambda<RelationalCommandResolver>(
            Call(
                commandLiftableConstant,
                _getRelationalCommandTemplateMethod ??=
                    typeof(RelationalCommandCache).GetMethod(nameof(RelationalCommandCache.GetRelationalCommandTemplate))!,
                parametersParameter),
            parametersParameter);

        bool TryGeneratePregeneratedCommandResolver(
            Expression select,
            [NotNullWhen(true)] out Expression<RelationalCommandResolver>? resolver)
        {
            var parameters = new Dictionary<string, object?>();
            var nullableParameterList = new List<SqlParameterExpression>();
            foreach (var parameter in new SqlParameterLocator().LocateParameters(select))
            {
                if (parameter.IsNullable)
                {
                    nullableParameterList.Add(parameter);
                    parameters[parameter.Name] = null;
                }
                else
                {
                    parameters[parameter.Name] = GenerateNonNullParameterValue(parameter.Type);
                }
            }

            var numNullableParameters = nullableParameterList.Count;

            if (numNullableParameters > MaxNullableParametersForPregeneratedSql)
            {
                resolver = null;
                return false;
            }

            var parameterDictionaryParameter = Parameter(typeof(IReadOnlyDictionary<string, object?>), "parameters");
            var resultParameter = Parameter(typeof(IRelationalCommandTemplate), "result");
            Expression resolverBody;
            bool canCache;

            if (numNullableParameters == 0)
            {
                resolverBody = GenerateRelationalCommandExpression(parameters, out canCache);
            }
            else
            {
                var parameterIndex = 0;

                resolverBody = Core(parameterIndex);
            }

            // If we can't cache the query SQL, we can't pregenerate it; flow down to the generic RelationalCommandCache path.
            // Note that in theory certain parameter nullability can be uncacheable, whereas others may be cacheable; so we could
            // keep pregenerated SQLs where that works, and flow down to the generic RelationalCommandCache path otherwise.
            if (!canCache)
            {
                resolver = null;
                return false;
            }

            resolver = Lambda<RelationalCommandResolver>(resolverBody, parameterDictionaryParameter);
            return true;

            Expression Core(int parameterIndex)
            {
                var currentParameter = nullableParameterList[parameterIndex];
                Expression ifNull, ifNotNull;
                ConditionalExpression ifThenElse;

                if (parameterIndex < numNullableParameters - 1)
                {
                    var parameter = nullableParameterList[parameterIndex];
                    parameters[parameter.Name] = null;
                    ifNull = Core(parameterIndex + 1);
                    if (!canCache)
                    {
                        return null!;
                    }

                    parameters[parameter.Name] = GenerateNonNullParameterValue(parameter.Type);
                    ifNotNull = Core(parameterIndex + 1);

                    ifThenElse =
                        IfThenElse(
                            Equal(
                                Property(parameterDictionaryParameter, "Item", Constant(currentParameter.Name)),
                                Constant(null, typeof(object))),
                            ifNull,
                            ifNotNull);
                }
                else
                {
                    // We've reached the last parameter; generate the SQL and see if we can cache it.
                    ifNull = LastParameter(withNull: true);
                    if (!canCache)
                    {
                        return null!;
                    }

                    ifNotNull = LastParameter(withNull: false);

                    ifThenElse =
                        IfThenElse(
                            Equal(
                                Property(parameterDictionaryParameter, "Item", Constant(currentParameter.Name)),
                                Constant(null, typeof(object))),
                            Assign(resultParameter, ifNull),
                            Assign(resultParameter, ifNotNull));
                }

                return parameterIndex > 0
                    ? Block(ifThenElse, resultParameter)
                    : Block(variables: [resultParameter], ifThenElse, resultParameter);

                Expression LastParameter(bool withNull)
                {
                    var parameter = nullableParameterList[parameterIndex];
                    parameters[parameter.Name] = withNull ? null : GenerateNonNullParameterValue(parameter.Type);

                    return GenerateRelationalCommandExpression(parameters, out canCache);
                }
            }

            static object GenerateNonNullParameterValue(Type type)
            {
                // In general, the (2nd part of) the query pipeline doesn't care about actual values - it mostly looks a null vs. non-null.
                // However, in some specific cases, it looks at actual parameters values - this happens e.g. for Contains over parameter, when
                // actual values are integrated into the SQL. For these cases, SQL can't be cached in any case and so pregeneration isn't
                // possible; but we still want to avoid casting exceptions, so we attempt to have a valid, correctly-typed value as the
                // parameter, and this method attempts to do that in a reasonable way.
                if (type == typeof(string))
                {
                    return string.Empty;
                }

                if (type.IsArray)
                {
                    return Array.CreateInstance(type.GetElementType()!, new int[type.GetArrayRank()]);
                }

                try
                {
                    return Activator.CreateInstance(type)!;
                }
                catch
                {
                    return new object();
                }
            }

            Expression GenerateRelationalCommandExpression(IReadOnlyDictionary<string, object?> parameters, out bool canCache)
            {
                var queryExpression = _relationalParameterBasedSqlProcessor.Optimize(select, parameters, out canCache);
                if (!canCache)
                {
                    return null!;
                }

                var relationalCommandTemplate = _querySqlGeneratorFactory.Create().GetCommand(queryExpression);

                var liftableConstantContextParameter = Parameter(typeof(RelationalMaterializerLiftableConstantContext), "c");
                // TODO: Instead of instantiating RelationalCommand directly go through the provider's RelationalCommandBuilder (#33516)
                return RelationalDependencies.RelationalLiftableConstantFactory.CreateLiftableConstant(
                    null!, // Not actually needed, as this is only used as a liftable constant
                    Lambda<Func<RelationalMaterializerLiftableConstantContext, object>>(
                        New(
                            _relationalCommandConstructor ??= typeof(RelationalCommand)
                                .GetConstructor(
                                [
                                    typeof(RelationalCommandBuilderDependencies),
                                    typeof(string),
                                    typeof(IReadOnlyList<IRelationalParameter>)
                                ])!,
                            Property(
                                liftableConstantContextParameter,
                                _commandBuilderDependenciesProperty ??= typeof(RelationalMaterializerLiftableConstantContext)
                                    .GetProperty(nameof(RelationalMaterializerLiftableConstantContext.CommandBuilderDependencies))!),
                            Constant(relationalCommandTemplate.CommandText),
                            NewArrayInit(
                                typeof(IRelationalParameter),
                                relationalCommandTemplate.Parameters.Cast<TypeMappedRelationalParameter>().Select(
                                    p => (Expression)New(
                                        _typeMappedRelationalParameterConstructor ??= typeof(TypeMappedRelationalParameter)
                                            .GetConstructor(
                                            [
                                                typeof(string),
                                                typeof(string),
                                                typeof(RelationalTypeMapping),
                                                typeof(bool?),
                                                typeof(ParameterDirection)
                                            ])!,
                                        Constant(p.InvariantName),
                                        Constant(p.Name),
                                        RelationalExpressionQuotingUtilities.QuoteTypeMapping((RelationalTypeMapping?)RelationalTypeMappingProp.GetValue(p)),
                                        Constant((bool?)IsNullableProp.GetValue(p), typeof(bool?)),
                                        Constant(p.Direction))).ToArray())),
                        liftableConstantContextParameter),
                    "relationalCommandTemplate",
                    typeof(IRelationalCommandTemplate));
            }
        }

        Expression<Func<RelationalMaterializerLiftableConstantContext, object>> GenerateRelationalCommandCacheExpression()
        {
            _hashSetConstructor ??= typeof(HashSet<string>).GetConstructor([typeof(IEnumerable<string>), typeof(StringComparer)])!;
            _stringComparerOrdinalProperty ??= typeof(StringComparer).GetProperty(nameof(StringComparer.Ordinal))!;
            _relationalCommandCacheConstructor ??= typeof(RelationalCommandCache).GetConstructors().Single();
            _dependenciesProperty ??=
                typeof(RelationalMaterializerLiftableConstantContext).GetProperty(
                    nameof(RelationalMaterializerLiftableConstantContext.Dependencies))!;
            _dependenciesMemoryCacheProperty ??=
                typeof(ShapedQueryCompilingExpressionVisitorDependencies).GetProperty(
                    nameof(ShapedQueryCompilingExpressionVisitorDependencies.MemoryCache))!;
            _relationalDependenciesProperty ??=
                typeof(RelationalMaterializerLiftableConstantContext).GetProperty(
                    nameof(RelationalMaterializerLiftableConstantContext.RelationalDependencies))!;
            _relationalDependenciesQuerySqlGeneratorFactoryProperty ??=
                typeof(RelationalShapedQueryCompilingExpressionVisitorDependencies).GetProperty(
                    nameof(RelationalShapedQueryCompilingExpressionVisitorDependencies.QuerySqlGeneratorFactory))!;
            _relationalDependenciesRelationalParameterBasedSqlProcessorFactoryProperty ??=
                typeof(RelationalShapedQueryCompilingExpressionVisitorDependencies).GetProperty(
                    nameof(RelationalShapedQueryCompilingExpressionVisitorDependencies.RelationalParameterBasedSqlProcessorFactory))!;

            var newHashSetExpression = New(
                _hashSetConstructor,
                NewArrayInit(typeof(string), _parametersToConstantize.Select(Constant)),
                MakeMemberAccess(null, _stringComparerOrdinalProperty));
            var contextParameter = Parameter(typeof(RelationalMaterializerLiftableConstantContext), "c");
            return
                Lambda<Func<RelationalMaterializerLiftableConstantContext, object>>(
                    New(
                        _relationalCommandCacheConstructor,
                        MakeMemberAccess(
                            MakeMemberAccess(contextParameter, _dependenciesProperty),
                            _dependenciesMemoryCacheProperty),
                        MakeMemberAccess(
                            MakeMemberAccess(contextParameter, _relationalDependenciesProperty),
                            _relationalDependenciesQuerySqlGeneratorFactoryProperty),
                        MakeMemberAccess(
                            MakeMemberAccess(contextParameter, _relationalDependenciesProperty),
                            _relationalDependenciesRelationalParameterBasedSqlProcessorFactoryProperty),
                        Constant(queryExpression),
                        Constant(_useRelationalNulls),
                        newHashSetExpression),
                    contextParameter);
        }
    }

    private static readonly PropertyInfo RelationalTypeMappingProp = typeof(TypeMappedRelationalParameter).GetProperty("RelationalTypeMapping",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetProperty)!;

    private static readonly PropertyInfo IsNullableProp = typeof(TypeMappedRelationalParameter).GetProperty("IsNullable",
        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetProperty)!;

    private sealed class SqlParameterLocator : ExpressionVisitor
    {
        private HashSet<SqlParameterExpression> _parameters = null!;

        public IReadOnlySet<SqlParameterExpression> LocateParameters(Expression selectExpression)
        {
            _parameters = new HashSet<SqlParameterExpression>();
            Visit(selectExpression);
            return _parameters;
        }

        protected override Expression VisitExtension(Expression node)
        {
            if (node is SqlParameterExpression parameter)
            {
                _parameters.Add(parameter);
            }

            return base.VisitExtension(node);
        }
    }
}
