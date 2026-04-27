using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ReturningExtensions.NonQueryPatch.MySql.Infrastructure;

public class MySqlQueryableMethodTranslatingExpressionVisitorFactoryPatch :
    RelationalQueryableMethodTranslatingExpressionVisitorFactory
{
    private readonly IModel _model;

    public MySqlQueryableMethodTranslatingExpressionVisitorFactoryPatch(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        IModel model)
    : base(dependencies, relationalDependencies)
    {
        _model = model;
    }

    public override QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
    {
        return new MySqlQueryableMethodTranslatingExpressionVisitorPatch(
            Dependencies,
            RelationalDependencies,
            queryCompilationContext,
            _model);
    }
}

public class MySqlQueryableMethodTranslatingExpressionVisitorPatch :
    RelationalQueryableMethodTranslatingExpressionVisitor
{
    private readonly IModel _model;
    private readonly RelationalSqlTranslatingExpressionVisitor _sqlTranslator;
    private readonly SharedTypeEntityExpandingExpressionVisitor _sharedTypeEntityExpandingExpressionVisitor;
    private readonly ISqlExpressionFactory _sqlExpressionFactory;

    public MySqlQueryableMethodTranslatingExpressionVisitorPatch(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        QueryCompilationContext queryCompilationContext,
        IModel model)
        : base(dependencies, relationalDependencies, queryCompilationContext)
    {
        _model = model;
        var sqlExpressionFactory = relationalDependencies.SqlExpressionFactory;
        _sqlExpressionFactory = sqlExpressionFactory;
        _sqlTranslator = relationalDependencies.RelationalSqlTranslatingExpressionVisitorFactory.Create(queryCompilationContext, this);
        _sharedTypeEntityExpandingExpressionVisitor =
            new SharedTypeEntityExpandingExpressionVisitor(_sqlTranslator, sqlExpressionFactory);
    }

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(RelationalQueryableExtensionsPatchExtensions))
        {
            var source = Visit(methodCallExpression.Arguments[0]);
            if (source is ShapedQueryExpression shapedQueryExpression)
            {
                var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
                switch (method.Name)
                {
                    case nameof(RelationalQueryableExtensionsPatchExtensions.ExecuteDelete)
                        when genericMethod == RelationalQueryableExtensionsPatchExtensions.ExecuteDeleteMethodInfoMarker:
                        return TranslateExecuteDelete(shapedQueryExpression)
                               ?? throw new ApplicationException();

                    case nameof(RelationalQueryableExtensionsPatchExtensions.ExecuteUpdate)
                        when genericMethod == RelationalQueryableExtensionsPatchExtensions.ExecuteUpdateMethodInfoMarker:
                        return TranslateExecuteUpdate(
                                   shapedQueryExpression,
                                   methodCallExpression.Arguments[1].UnwrapLambdaFromQuote())
                               ?? throw new ApplicationException();
                }
            }
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    protected virtual NonQueryExpression? TranslateExecuteDelete(ShapedQueryExpression source)
    {
        if (source.ShaperExpression is IncludeExpression includeExpression)
        {
            source = source.UpdateShaperExpression(PruneIncludes(includeExpression));
        }

        if (source.ShaperExpression is not EntityShaperExpression entityShaperExpression)
        {
            return null;
        }

        var entityType = entityShaperExpression.EntityType;
        var selectExpression = (SelectExpression)source.QueryExpression;

        if (IsValidSelectExpressionForExecuteDelete(selectExpression, entityShaperExpression, out var tableExpression))
        {
            if (AreOtherNonOwnedEntityTypesInTheTable(entityType.GetRootType(), tableExpression.Table))
            {
                AddTranslationErrorDetails(
                    "RelationalStrings.ExecuteDeleteOnTableSplitting(tableExpression.Table.SchemaQualifiedName)");

                return null;
            }

            //selectExpression.ReplaceProjection(new List<Expression>());
            //selectExpression.ApplyProjection();

            return new NonQueryExpression(new DeleteExpression(tableExpression, selectExpression));

            static bool AreOtherNonOwnedEntityTypesInTheTable(IEntityType rootType, ITableBase table)
            {
                foreach (var entityTypeMapping in table.EntityTypeMappings)
                {
                    var typeBase = entityTypeMapping.EntityType;
                    if ((entityTypeMapping.IsSharedTablePrincipal == true
                            && typeBase != rootType)
                        || (entityTypeMapping.IsSharedTablePrincipal == false
                            && typeBase is IEntityType entityType
                            && entityType.GetRootType() != rootType
                            && !entityType.IsOwned()))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // The provider doesn't natively support the delete.
        // As a fallback, we place the original query in a Contains subquery, which will get translated via the regular entity equality/
        // containment mechanism (InExpression for non-composite keys, Any for composite keys)
        var pk = entityType.FindPrimaryKey();
        if (pk == null)
        {
            AddTranslationErrorDetails(
                @"RelationalStrings.ExecuteOperationOnKeylessEntityTypeWithUnsupportedOperator(
                    nameof(RelationalQueryableExtensions.ExecuteDelete),
                    entityType.DisplayName())");
            return null;
        }

        var clrType = entityType.ClrType;
        var entityParameter = Expression.Parameter(clrType);
        var predicateBody = Expression.Call(QueryableMethods.Contains.MakeGenericMethod(clrType), source, entityParameter);

        var newSource = Expression.Call(
            QueryableMethods.Where.MakeGenericMethod(clrType),
            new EntityQueryRootExpression(entityType),
            Expression.Quote(Expression.Lambda(predicateBody, entityParameter)));

        return TranslateExecuteDelete((ShapedQueryExpression)Visit(newSource));
    }

    private static Expression PruneIncludes(IncludeExpression includeExpression)
    {
        if (includeExpression.Navigation is ISkipNavigation or not INavigation)
        {
            return includeExpression;
        }

        return includeExpression.EntityExpression is IncludeExpression innerIncludeExpression
            ? PruneIncludes(innerIncludeExpression)
            : includeExpression.EntityExpression;
    }

    protected virtual bool IsValidSelectExpressionForExecuteDelete(
        SelectExpression selectExpression,
        EntityShaperExpression shaper,
        out TableExpression tableExpression)
    {
        if (selectExpression.Offset == null
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Having == null
            && (selectExpression.Tables.Count == 1 || selectExpression.Orderings.Count == 0))
        {
            TableExpressionBase table;
            if (selectExpression.Tables.Count == 1)
            {
                table = selectExpression.Tables[0];
            }
            else
            {
                var projectionBindingExpression = (ProjectionBindingExpression)shaper.ValueBufferExpression;
                var entityProjectionExpression =
                    (EntityProjectionExpression)selectExpression.GetProjection(projectionBindingExpression);
                var property = shaper.EntityType.GetProperties().First();
                ColumnExpression column = entityProjectionExpression.BindProperty(property);

                table = column.Table;
                if (table is JoinExpressionBase joinExpressionBase)
                {
                    table = joinExpressionBase.Table;
                }
            }

            if (table is TableExpression te)
            {
                tableExpression = te;
                return true;
            }
        }

        tableExpression = null;
        return false;
    }

    protected virtual bool IsValidSelectExpressionForExecuteUpdate(
        SelectExpression selectExpression,
        TableExpressionBase targetTable,
        [NotNullWhen(true)] out TableExpression? tableExpression)
    {
        tableExpression = null;
        if (selectExpression is
            {
                Offset: null,
                Limit: null,
                IsDistinct: false,
                GroupBy: [],
                Having: null,
                Orderings: [],
                Tables.Count: > 0
            })
        {
            if (selectExpression.Tables.Count > 1)
            {
                // If the table we are looking for is the first table, then we need to verify whether we can lift the next table in FROM
                // clause
                if (ReferenceEquals(selectExpression.Tables[0], targetTable)
                    && selectExpression.Tables[1] is not InnerJoinExpression and not CrossJoinExpression)
                {
                    return false;
                }

                if (targetTable is JoinExpressionBase joinExpressionBase)
                {
                    targetTable = joinExpressionBase.Table;
                }
            }

            if (targetTable is TableExpression te)
            {
                tableExpression = te;
                return true;
            }
        }

        return false;
    }


    protected virtual NonQueryExpression? TranslateExecuteUpdate(ShapedQueryExpression source, LambdaExpression setPropertyCalls)
    {
        // Our source may have IncludeExpressions because of owned entities or auto-include; unwrap these, as they're meaningless for
        // ExecuteUpdate's lambdas. Note that we don't currently support updates across tables.
        if (source.ShaperExpression is IncludeExpression includeExpression)
        {
            source = source.UpdateShaperExpression(PruneIncludes(includeExpression));
        }

        var propertyValueLambdaExpressions = new List<(LambdaExpression PropertySelector, Expression ValueExpression)>();
        PopulateSetPropertyCalls(setPropertyCalls.Body, propertyValueLambdaExpressions, setPropertyCalls.Parameters[0]);
        if (TranslationErrorDetails != null)
        {
            return null;
        }

        if (propertyValueLambdaExpressions.Count == 0)
        {
            AddTranslationErrorDetails("RelationalStrings.NoSetPropertyInvocation");
            return null;
        }

        // Go over the SetProperty calls, and translate the property selectors (left lambda).
        // The property selectors should get translated to ColumnExpressions (otherwise they' invalid - columns are what we need to update).
        // All columns must also refer to the same table (since that's how SQL UPDATE works), extract that target table from the translated
        // columns and validate that only one table is being referenced.
        // Note that we don't translate the value expressions in this pass, since if the query is complicated, we may need to do a pushdown
        // (see PushdownWithPkInnerJoinPredicate below); so we defer translation until we have the final source/select. For the property
        // selectors we need to translate now since we need the table.
        TableExpressionBase? targetTable = null;
        Expression? targetTablePropertySelector = null;
        var columns = new ColumnExpression[propertyValueLambdaExpressions.Count];
        for (var i = 0; i < propertyValueLambdaExpressions.Count; i++)
        {
            var (propertySelector, _) = propertyValueLambdaExpressions[i];
            var propertySelectorBody = RemapLambdaBody(source, propertySelector).UnwrapTypeConversion(out _);
            if (_sqlTranslator.Translate(propertySelectorBody) is not ColumnExpression column)
            {
                AddTranslationErrorDetails("RelationalStrings.InvalidPropertyInSetProperty(propertySelector.Print())");
                return null;
            }

            if (targetTable is null)
            {
                targetTable = column.Table;
                targetTablePropertySelector = propertySelector;
            }
            else if (!ReferenceEquals(column.Table, targetTable))
            {
                AddTranslationErrorDetails(
                    @"RelationalStrings.MultipleTablesInExecuteUpdate(propertySelector.Print(), targetTablePropertySelector!.Print())");
                return null;
            }

            columns[i] = column;
        }

        // First, check if the provider has a native translation for the update represented by the select expression.
        // The default relational implementation handles simple, universally-supported cases (i.e. no operators except for predicate).
        // Providers may override IsValidSelectExpressionForExecuteUpdate to add support for more cases via provider-specific UPDATE syntax.
        var selectExpression = (SelectExpression)source.QueryExpression;
        return IsValidSelectExpressionForExecuteUpdate(selectExpression, targetTable, out var tableExpression)
            ? TranslateValueExpressions(this, source, selectExpression, tableExpression, propertyValueLambdaExpressions, columns)
            : PushdownWithPkInnerJoinPredicate();

        void PopulateSetPropertyCalls(
            Expression expression,
            List<(LambdaExpression, Expression)> list,
            ParameterExpression parameter)
        {
            switch (expression)
            {
                case ParameterExpression p
                    when parameter == p:
                    break;

                case MethodCallExpression
                {
                    Method:
                    {
                        IsGenericMethod: true,
                        Name: nameof(SetPropertyCalls<int>.SetProperty),
                        DeclaringType.IsGenericType: true
                    }
                } methodCallExpression
                    when methodCallExpression.Method.DeclaringType.GetGenericTypeDefinition() == typeof(SetPropertyCalls<>):
                    list.Add(((LambdaExpression)methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]));

                    PopulateSetPropertyCalls(methodCallExpression.Object!, list, parameter);

                    break;

                default:
                    AddTranslationErrorDetails("RelationalStrings.InvalidArgumentToExecuteUpdate");
                    break;
            }
        }

        NonQueryExpression? TranslateValueExpressions(
            RelationalQueryableMethodTranslatingExpressionVisitor visitor,
            ShapedQueryExpression source,
            SelectExpression selectExpression,
            TableExpression tableExpression,
            List<(LambdaExpression PropertySelector, Expression ValueExpression)> propertyValueLambdaExpression,
            ColumnExpression[] columns)
        {
            var setters = new ColumnValueSetter[columns.Length];

            for (var i = 0; i < propertyValueLambdaExpression.Count; i++)
            {
                var column = columns[i];
                var (_, valueSelector) = propertyValueLambdaExpression[i];

                var remappedValueSelector = valueSelector is LambdaExpression lambdaExpression
                    ? RemapLambdaBody(source, lambdaExpression)
                    : valueSelector;

                if (remappedValueSelector.Type != column.Type)
                {
                    remappedValueSelector = Expression.Convert(remappedValueSelector, column.Type);
                }

                if (TranslateExpression(remappedValueSelector)
                    is not SqlExpression translatedValueSelector)
                {
                    AddTranslationErrorDetails("RelationalStrings.InvalidValueInSetProperty(valueSelector.Print())");
                    return null;
                }

                // Apply the type mapping of the column (translated from the property selector above) to the value,
                // and apply alias uniquification to it.
                translatedValueSelector = _sqlExpressionFactory.ApplyTypeMapping(translatedValueSelector, column.TypeMapping);
                var _usedAliases = (HashSet<string>)typeof(SelectExpression).GetField("_usedAliases", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetProperty)!.GetValue(selectExpression)!;
                translatedValueSelector = (SqlExpression)new AliasUniquifier(_usedAliases).Visit(translatedValueSelector);

                setters[i] = new ColumnValueSetter(column, translatedValueSelector);
            }

            //selectExpression.ReplaceProjection(new List<Expression>());
            //selectExpression.ApplyProjection();

            return new NonQueryExpression(new UpdateExpression(tableExpression, selectExpression, setters));
        }

        NonQueryExpression? PushdownWithPkInnerJoinPredicate()
        {
            // The provider doesn't natively support the update.
            // As a fallback, we place the original query in a subquery and user an INNER JOIN on the primary key columns.

            // Note that unlike with ExecuteDelete, we cannot use a Contains subquery (which would produce the simpler
            // WHERE Id IN (SELECT ...) syntax), since we allow projecting out to arbitrary shapes (e.g. anonymous types) before the
            // ExecuteUpdate.

            // To rewrite the query, we need to know the primary key properties, which requires getting the entity type.
            // Although there may be several entity types involved, we've already verified that they all map to the same table.
            // Since we don't support table sharing of multiple entity types with different keys, simply get the entity type and key from
            // the first property selector.

            // The following mechanism for extracting the entity type from property selectors only supports simple member access,
            // EF.Function, etc. We also unwrap casts to interface/base class (#29618). Note that owned IncludeExpressions have already
            // been pruned from the source before remapping the lambda (#28727).

            var firstPropertySelector = propertyValueLambdaExpressions[0].PropertySelector;
            var shaper = RemapLambdaBody(source, firstPropertySelector).UnwrapTypeConversion(out _) switch
            {
                MemberExpression { Expression: not null } memberExpression
                    when memberExpression.Expression.UnwrapTypeConversion(out _) is EntityShaperExpression s
                    => s,

                MethodCallExpression mce when mce.TryGetEFPropertyArguments(out var source, out _)
                    && source.UnwrapTypeConversion(out _) is EntityShaperExpression s
                    => s,

                MethodCallExpression mce when mce.TryGetIndexerArguments(_model, out var source2, out _)
                    && source2.UnwrapTypeConversion(out _) is EntityShaperExpression s
                    => s,

                _ => null
            };

            if (shaper is null)
            {
                AddTranslationErrorDetails("RelationalStrings.InvalidPropertyInSetProperty(firstPropertySelector)");
                return null;
            }

            if (shaper.EntityType is not IEntityType entityType)
            {
                AddTranslationErrorDetails(
                    "RelationalStrings.ExecuteUpdateSubqueryNotSupportedOverComplexTypes(shaper.StructuralType.DisplayName())");
                return null;
            }

            if (entityType.FindPrimaryKey() is not IKey pk)
            {
                AddTranslationErrorDetails(
                    @"RelationalStrings.ExecuteOperationOnKeylessEntityTypeWithUnsupportedOperator(
                        nameof(RelationalQueryableExtensions.ExecuteUpdate),
                        entityType.DisplayName())");
                return null;
            }

            // Generate the INNER JOIN around the original query, on the PK properties.
            var outer = (ShapedQueryExpression)Visit(new EntityQueryRootExpression(entityType));
            var inner = source;
            var outerParameter = Expression.Parameter(entityType.ClrType);
            var outerKeySelector = Expression.Lambda(outerParameter.CreateKeyValuesExpression(pk.Properties), outerParameter);
            var firstPropertyLambdaExpression = propertyValueLambdaExpressions[0].Item1;
            var entitySource = GetEntitySource(_model, firstPropertyLambdaExpression.Body);
            var innerKeySelector = Expression.Lambda(
                entitySource.CreateKeyValuesExpression(pk.Properties), firstPropertyLambdaExpression.Parameters);

            var joinPredicate = CreateJoinPredicate(outer, outerKeySelector, inner, innerKeySelector);

            var outerSelectExpression = (SelectExpression)outer.QueryExpression;
            var outerShaperExpression = outerSelectExpression.AddInnerJoin(inner, joinPredicate, outer.ShaperExpression);
            outer = outer.UpdateShaperExpression(outerShaperExpression);
            var transparentIdentifierType = outer.ShaperExpression.Type;
            var transparentIdentifierParameter = Expression.Parameter(transparentIdentifierType);

            var propertyReplacement = AccessField(transparentIdentifierType, transparentIdentifierParameter, "Outer");
            var valueReplacement = AccessField(transparentIdentifierType, transparentIdentifierParameter, "Inner");
            for (var i = 0; i < propertyValueLambdaExpressions.Count; i++)
            {
                var (propertyExpression, valueExpression) = propertyValueLambdaExpressions[i];
                propertyExpression = Expression.Lambda(
                    ReplacingExpressionVisitor.Replace(
                        ReplacingExpressionVisitor.Replace(
                            firstPropertyLambdaExpression.Parameters[0],
                            propertyExpression.Parameters[0],
                            entitySource),
                        propertyReplacement, propertyExpression.Body),
                    transparentIdentifierParameter);

                valueExpression = valueExpression is LambdaExpression lambdaExpression
                    ? Expression.Lambda(
                        ReplacingExpressionVisitor.Replace(lambdaExpression.Parameters[0], valueReplacement, lambdaExpression.Body),
                        transparentIdentifierParameter)
                    : valueExpression;

                propertyValueLambdaExpressions[i] = (propertyExpression, valueExpression);
            }

            tableExpression = (TableExpression)outerSelectExpression.Tables[0];

            // Re-translate the property selectors to get column expressions pointing to the new outer select expression (the original one
            // has been pushed down into a subquery).
            for (var i = 0; i < propertyValueLambdaExpressions.Count; i++)
            {
                var (propertySelector, _) = propertyValueLambdaExpressions[i];
                var propertySelectorBody = RemapLambdaBody(outer, propertySelector).UnwrapTypeConversion(out _);

                if (TranslateExpression(propertySelectorBody) is not ColumnExpression column)
                {
                    AddTranslationErrorDetails("RelationalStrings.InvalidPropertyInSetProperty(propertySelector.Print())");
                    return null;
                }

                columns[i] = column;
            }

            return TranslateValueExpressions(this, outer, outerSelectExpression, tableExpression, propertyValueLambdaExpressions, columns);
        }

        static Expression GetEntitySource(IModel model, Expression propertyAccessExpression)
        {
            propertyAccessExpression = propertyAccessExpression.UnwrapTypeConversion(out _);
            if (propertyAccessExpression is MethodCallExpression mce)
            {
                if (mce.TryGetEFPropertyArguments(out var source, out _))
                {
                    return source;
                }

                if (mce.TryGetIndexerArguments(model, out var source2, out _))
                {
                    return source2;
                }
            }

            return ((MemberExpression)propertyAccessExpression).Expression!;
        }
    }

    private static Expression AccessField(
        Type transparentIdentifierType,
        Expression targetExpression,
        string fieldName)
        => Expression.Field(targetExpression, transparentIdentifierType.GetTypeInfo().GetDeclaredField(fieldName)!);

    private Expression RemapLambdaBody(ShapedQueryExpression shapedQueryExpression, LambdaExpression lambdaExpression)
    {
        var lambdaBody = ReplacingExpressionVisitor.Replace(
            lambdaExpression.Parameters.Single(), shapedQueryExpression.ShaperExpression, lambdaExpression.Body);

        return ExpandSharedTypeEntities((SelectExpression)shapedQueryExpression.QueryExpression, lambdaBody);
    }

    private Expression ExpandSharedTypeEntities(SelectExpression selectExpression, Expression lambdaBody)
        => _sharedTypeEntityExpandingExpressionVisitor.Expand(selectExpression, lambdaBody);

    private SqlExpression CreateJoinPredicate(
        ShapedQueryExpression outer,
        LambdaExpression outerKeySelector,
        ShapedQueryExpression inner,
        LambdaExpression innerKeySelector)
    {
        var outerKey = RemapLambdaBody(outer, outerKeySelector);
        var innerKey = RemapLambdaBody(inner, innerKeySelector);

        if (outerKey is NewExpression outerNew
            && outerNew.Arguments.Count > 0)
        {
            var innerNew = (NewExpression)innerKey;

            SqlExpression? result = null;
            for (var i = 0; i < outerNew.Arguments.Count; i++)
            {
                var joinPredicate = CreateJoinPredicate(outerNew.Arguments[i], innerNew.Arguments[i]);
                result = result == null
                    ? joinPredicate
                    : _sqlExpressionFactory.AndAlso(result, joinPredicate);
            }

            if (outerNew.Arguments.Count == 1)
            {
                result = _sqlExpressionFactory.AndAlso(
                    result!,
                    CreateJoinPredicate(Expression.Constant(true), Expression.Constant(true)));
            }

            return result!;
        }

        return CreateJoinPredicate(outerKey, innerKey);
    }

    private SqlExpression CreateJoinPredicate(Expression outerKey, Expression innerKey)
        => TranslateExpression(CreateEqualsExpression(outerKey, innerKey))!;

    protected virtual SqlExpression? TranslateExpression(Expression expression)
    {
        var translation = _sqlTranslator.Translate(expression);

        if (translation is null)
        {
            if (_sqlTranslator.TranslationErrorDetails != null)
            {
                AddTranslationErrorDetails(_sqlTranslator.TranslationErrorDetails);
            }
        }

        return translation;
    }


    private static readonly MethodInfo ObjectEqualsMethodInfo
        = typeof(object).GetRuntimeMethod(nameof(object.Equals), new[] { typeof(object), typeof(object) })!;

    private static Expression CreateEqualsExpression(
        Expression left,
        Expression right,
        bool negated = false)
    {
        var result = Expression.Call(ObjectEqualsMethodInfo, AddConvertToObject(left), AddConvertToObject(right));

        return negated
            ? Expression.Not(result)
            : result;

        static Expression AddConvertToObject(Expression expression)
            => expression.Type.IsValueType
                ? Expression.Convert(expression, typeof(object))
                : expression;
    }

    private sealed class SharedTypeEntityExpandingExpressionVisitor : ExpressionVisitor
    {
        private readonly RelationalSqlTranslatingExpressionVisitor _sqlTranslator;
        private readonly ISqlExpressionFactory _sqlExpressionFactory;

        private SelectExpression _selectExpression;

        public SharedTypeEntityExpandingExpressionVisitor(
            RelationalSqlTranslatingExpressionVisitor sqlTranslator,
            ISqlExpressionFactory sqlExpressionFactory)
        {
            _sqlTranslator = sqlTranslator;
            _sqlExpressionFactory = sqlExpressionFactory;
            _selectExpression = null!;
        }

        public Expression Expand(SelectExpression selectExpression, Expression lambdaBody)
        {
            _selectExpression = selectExpression;

            return Visit(lambdaBody);
        }

        protected override Expression VisitMember(MemberExpression memberExpression)
        {
            var innerExpression = Visit(memberExpression.Expression);

            return TryExpand(innerExpression, MemberIdentity.Create(memberExpression.Member))
                ?? memberExpression.Update(innerExpression);
        }

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var navigationName))
            {
                source = Visit(source);

                return TryExpand(source, MemberIdentity.Create(navigationName))
                    ?? methodCallExpression.Update(null!, new[] { source, methodCallExpression.Arguments[1] });
            }

            return base.VisitMethodCall(methodCallExpression);
        }

        protected override Expression VisitExtension(Expression extensionExpression)
            => extensionExpression is EntityShaperExpression
                || extensionExpression is ShapedQueryExpression
                || extensionExpression is GroupByShaperExpression
                    ? extensionExpression
                    : base.VisitExtension(extensionExpression);

        private Expression? TryExpand(Expression? source, MemberIdentity member)
        {
            source = source.UnwrapTypeConversion(out var convertedType);
            if (source is not EntityShaperExpression entityShaperExpression)
            {
                return null;
            }

            var entityType = entityShaperExpression.EntityType;
            if (convertedType != null)
            {
                entityType = entityType.GetRootType().GetDerivedTypesInclusive()
                    .FirstOrDefault(et => et.ClrType == convertedType);

                if (entityType == null)
                {
                    return null;
                }
            }

            var navigation = member.MemberInfo != null
                ? entityType.FindNavigation(member.MemberInfo)
                : entityType.FindNavigation(member.Name!);

            if (navigation == null)
            {
                return null;
            }

            var targetEntityType = navigation.TargetEntityType;
            if (targetEntityType == null
                || !targetEntityType.IsOwned())
            {
                return null;
            }

            if (TryGetJsonQueryExpression(entityShaperExpression, out var jsonQueryExpression))
            {
                var newJsonQueryExpression = jsonQueryExpression.BindNavigation(navigation);

                return navigation.IsCollection
                    ? newJsonQueryExpression
                    : new RelationalEntityShaperExpression(
                        navigation.TargetEntityType,
                        newJsonQueryExpression,
                        nullable: entityShaperExpression.IsNullable || !navigation.ForeignKey.IsRequired);
            }

            var entityProjectionExpression = GetEntityProjectionExpression(entityShaperExpression);
            var foreignKey = navigation.ForeignKey;

            if (targetEntityType.IsMappedToJson())
            {
                var innerShaper = entityProjectionExpression.BindNavigation(navigation)!;

                return navigation.IsCollection
                    ? (JsonQueryExpression)innerShaper.ValueBufferExpression
                    : innerShaper;
            }

            if (navigation.IsCollection)
            {
                // just need any column - we use it only to extract the table it originated from
                var sourceColumn = entityProjectionExpression
                    .BindProperty(
                        navigation.IsOnDependent
                            ? foreignKey.Properties[0]
                            : foreignKey.PrincipalKey.Properties[0]);

                var sourceTable = FindRootTableExpressionForColumn(sourceColumn);
                var innerSelectExpression = _sqlExpressionFactory.Select(targetEntityType);
#if NET7_0_OR_GREATER
                innerSelectExpression = (SelectExpression)new AnnotationApplyingExpressionVisitor(sourceTable.GetAnnotations().ToList())
                    .Visit(innerSelectExpression);
#endif
                var innerShapedQuery = CreateShapedQueryExpression(targetEntityType, innerSelectExpression);

                var makeNullable = foreignKey.PrincipalKey.Properties
                    .Concat(foreignKey.Properties)
                    .Select(p => p.ClrType)
                    .Any(t => t.IsNullableType());

                var innerSequenceType = innerShapedQuery.Type.GetSequenceType();
                var correlationPredicateParameter = Expression.Parameter(innerSequenceType);

                var outerKey = entityShaperExpression.CreateKeyValuesExpression(
                    navigation.IsOnDependent
                        ? foreignKey.Properties
                        : foreignKey.PrincipalKey.Properties,
                    makeNullable);
                var innerKey = correlationPredicateParameter.CreateKeyValuesExpression(
                    navigation.IsOnDependent
                        ? foreignKey.PrincipalKey.Properties
                        : foreignKey.Properties,
                    makeNullable);

                var keyComparison = CreateEqualsExpression(outerKey, innerKey);

                var predicate = makeNullable
                    ? Expression.AndAlso(
                        outerKey is NewArrayExpression newArrayExpression
                            ? newArrayExpression.Expressions
                                .Select(
                                    e =>
                                    {
                                        var left = (e as UnaryExpression)?.Operand ?? e;

                                        return Expression.NotEqual(left, Expression.Constant(null, left.Type));
                                    })
                                .Aggregate((l, r) => Expression.AndAlso(l, r))
                            : Expression.NotEqual(outerKey, Expression.Constant(null, outerKey.Type)),
                        keyComparison)
                    : keyComparison;

                var correlationPredicate = Expression.Lambda(predicate, correlationPredicateParameter);

                return Expression.Call(
                    QueryableMethods.Where.MakeGenericMethod(innerSequenceType),
                    innerShapedQuery,
                    Expression.Quote(correlationPredicate));
            }

            return entityProjectionExpression.BindNavigation(navigation)
#if NET7_0_OR_GREATER             
                   ?? _selectExpression.GenerateOwnedReferenceEntityProjectionExpression(
                    entityProjectionExpression, navigation, _sqlExpressionFactory)
#endif
                ;

            static TableExpressionBase FindRootTableExpressionForColumn(ColumnExpression column)
            {
                var table = column.Table;
                if (table is JoinExpressionBase joinExpressionBase)
                {
                    table = joinExpressionBase.Table;
                }
                else if (table is SetOperationBase setOperationBase)
                {
                    table = setOperationBase.Source1;
                }

                if (table is SelectExpression selectExpression)
                {
                    var matchingProjection =
                        (ColumnExpression)selectExpression.Projection.Where(p => p.Alias == column.Name).Single().Expression;

                    return FindRootTableExpressionForColumn(matchingProjection);
                }

                return table;
            }
        }

        private sealed class AnnotationApplyingExpressionVisitor : ExpressionVisitor
        {
            private readonly IReadOnlyList<IAnnotation> _annotations;

            public AnnotationApplyingExpressionVisitor(IReadOnlyList<IAnnotation> annotations)
            {
                _annotations = annotations;
            }

            [return: NotNullIfNotNull("expression")]
            public override Expression? Visit(Expression? expression)
            {
                if (expression is TableExpression te)
                {
                    TableExpressionBase ownedTable = te;
                    foreach (var annotation in _annotations)
                    {
#if NET7_0_OR_GREATER
                        ownedTable = ownedTable.AddAnnotation(annotation.Name, annotation.Value);
#endif
                    }

                    return ownedTable;
                }

                return base.Visit(expression);
            }
        }

        private bool TryGetJsonQueryExpression(
            EntityShaperExpression entityShaperExpression,
            [NotNullWhen(true)] out JsonQueryExpression? jsonQueryExpression)
        {
            switch (entityShaperExpression.ValueBufferExpression)
            {
                case ProjectionBindingExpression projectionBindingExpression:
                    jsonQueryExpression = _selectExpression.GetProjection(projectionBindingExpression) as JsonQueryExpression;
                    return jsonQueryExpression != null;

                case JsonQueryExpression jqe:
                    jsonQueryExpression = jqe;
                    return true;

                default:
                    jsonQueryExpression = null;
                    return false;
            }
        }
        private EntityProjectionExpression GetEntityProjectionExpression(EntityShaperExpression entityShaperExpression)
            => entityShaperExpression.ValueBufferExpression switch
            {
                ProjectionBindingExpression projectionBindingExpression
                    => (EntityProjectionExpression)_selectExpression.GetProjection(projectionBindingExpression),
                EntityProjectionExpression entityProjectionExpression => entityProjectionExpression,
                _ => throw new InvalidOperationException()
            };
    }

    private static ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType, SelectExpression selectExpression)
        => new(
            selectExpression,
            new RelationalEntityShaperExpression(
                entityType,
                new ProjectionBindingExpression(
                    selectExpression,
                    new ProjectionMember(),
                    typeof(ValueBuffer)),
                false));

}

internal sealed class AliasUniquifier : ExpressionVisitor
{
    private readonly HashSet<string> _usedAliases;
    private readonly List<SelectExpression> _visitedSelectExpressions = new();

    public AliasUniquifier(HashSet<string> usedAliases)
    {
        _usedAliases = usedAliases;
    }

    [return: NotNullIfNotNull("expression")]
    public override Expression? Visit(Expression? expression)
    {
        if (expression is SelectExpression innerSelectExpression
            && !_visitedSelectExpressions.Contains(innerSelectExpression))
        {
            for (var i = 0; i < Get_tableReferences(innerSelectExpression).Count; i++)
            {
                var currentAlias = Get_tableReferences(innerSelectExpression)[i].Alias;
                var newAlias = GenerateUniqueAlias(_usedAliases, currentAlias);

                if (newAlias != currentAlias)
                {
                    // we keep the old alias in the list (even though it's not actually being used anymore)
                    // to disambiguate the APPLY case, e.g. something like this:
                    // SELECT * FROM EntityOne as e
                    // OUTER APPLY (
                    //    SELECT * FROM EntityTwo as e1
                    //    LEFT JOIN EntityThree as e ON (...) -- reuse alias e, since we use e1 after uniquification
                    //    WHERE e.Foo == e1.Bar -- ambiguity! e could refer to EntityOne or EntityThree
                    // ) as t
                    Get_usedAliases(innerSelectExpression).Add(newAlias);
                }

                Get_tableReferences(innerSelectExpression)[i].Alias = newAlias;
                var table = UnwrapJoinExpression(Get_tables(innerSelectExpression)[i]);
                SetAlias(table, newAlias);
            }

            _visitedSelectExpressions.Add(innerSelectExpression);
        }

        return base.Visit(expression);
    }

    // List<TableReferenceExpression> _tableReferences
    private static dynamic Get_tableReferences(SelectExpression expression)
    {
        return typeof(SelectExpression).GetField("_tableReferences", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!
            .GetValue(expression)!;
    }

    private static List<TableExpressionBase> Get_tables(SelectExpression expression)
    {
        return (List<TableExpressionBase>)typeof(SelectExpression).GetField("_tables", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!
            .GetValue(expression)!;
    }

    private static HashSet<string> Get_usedAliases(SelectExpression expression)
    {
        return (HashSet<string>)typeof(SelectExpression).GetField("_usedAliases", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!
            .GetValue(expression)!;
    }

    private static void SetAlias(TableExpressionBase expression, string alias)
    {
        var p = typeof(TableExpressionBase).GetProperty(nameof(TableExpressionBase.Alias), BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetProperty)!;
        p.SetValue(expression, alias);
    }

    private static string GenerateUniqueAlias(HashSet<string> usedAliases, string currentAlias)
    {
        var counter = 0;
        var baseAlias = currentAlias[..1];

        while (usedAliases.Contains(currentAlias))
        {
            currentAlias = baseAlias + counter++;
        }

        usedAliases.Add(currentAlias);
        return currentAlias;
    }

    private static TableExpressionBase UnwrapJoinExpression(TableExpressionBase tableExpressionBase)
        => (tableExpressionBase as JoinExpressionBase)?.Table ?? tableExpressionBase;
}

internal static class ExpressionExtensions
{
    public static RelationalTypeMapping? GetContainerColumnTypeMapping(this IReadOnlyEntityType entityType)
        => entityType.FindAnnotation("RelationalAnnotationNames.ContainerColumnTypeMapping")?.Value is RelationalTypeMapping typeMapping
            ? typeMapping
            : (entityType.FindOwnership()?.PrincipalEntityType.GetContainerColumnTypeMapping());


    public static readonly MethodInfo ValueBufferTryReadValueMethod
        = typeof(ExpressionExtensions).GetTypeInfo().GetDeclaredMethod(nameof(ValueBufferTryReadValue))!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TValue ValueBufferTryReadValue<TValue>(
#pragma warning disable IDE0060 // Remove unused parameter
        in ValueBuffer valueBuffer,
        int index,
        IPropertyBase property)
#pragma warning restore IDE0060 // Remove unused parameter
        => (TValue)valueBuffer[index]!;

    public static bool IsNullConstantExpression(this Expression expression)
        => RemoveConvert(expression) is ConstantExpression constantExpression
            && constantExpression.Value == null;

    public static LambdaExpression UnwrapLambdaFromQuote(this Expression expression)
        => (LambdaExpression)(expression is UnaryExpression unary && expression.NodeType == ExpressionType.Quote
            ? unary.Operand
            : expression);

    [return: NotNullIfNotNull("expression")]
    public static Expression? UnwrapTypeConversion(this Expression? expression, out Type? convertedType)
    {
        convertedType = null;
        while (expression is UnaryExpression unaryExpression
               && (unaryExpression.NodeType == ExpressionType.Convert
                   || unaryExpression.NodeType == ExpressionType.ConvertChecked
                   || unaryExpression.NodeType == ExpressionType.TypeAs))
        {
            expression = unaryExpression.Operand;
            if (unaryExpression.Type != typeof(object) // Ignore object conversion
                && !unaryExpression.Type.IsAssignableFrom(expression.Type)) // Ignore casting to base type/interface
            {
                convertedType = unaryExpression.Type;
            }
        }

        return expression;
    }

    private static Expression RemoveConvert(Expression expression)
    {
        if (expression is UnaryExpression unaryExpression
            && (expression.NodeType == ExpressionType.Convert
                || expression.NodeType == ExpressionType.ConvertChecked))
        {
            return RemoveConvert(unaryExpression.Operand);
        }

        return expression;
    }

    public static T GetConstantValue<T>(this Expression expression)
        => expression is ConstantExpression constantExpression
            ? (T)constantExpression.Value!
            : throw new InvalidOperationException();
}

internal static class SharedTypeExtensions
{
    private static readonly Dictionary<Type, string> BuiltInTypeNames = new()
    {
        { typeof(bool), "bool" },
        { typeof(byte), "byte" },
        { typeof(char), "char" },
        { typeof(decimal), "decimal" },
        { typeof(double), "double" },
        { typeof(float), "float" },
        { typeof(int), "int" },
        { typeof(long), "long" },
        { typeof(object), "object" },
        { typeof(sbyte), "sbyte" },
        { typeof(short), "short" },
        { typeof(string), "string" },
        { typeof(uint), "uint" },
        { typeof(ulong), "ulong" },
        { typeof(ushort), "ushort" },
        { typeof(void), "void" }
    };

    public static Type UnwrapNullableType(this Type type)
        => Nullable.GetUnderlyingType(type) ?? type;

    public static bool IsNullableValueType(this Type type)
        => type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);

    public static bool IsNullableType(this Type type)
        => !type.IsValueType || type.IsNullableValueType();

    public static bool IsValidEntityType(this Type type)
        => type.IsClass
            && !type.IsArray;

    public static bool IsPropertyBagType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type type)
    {
        if (type.IsGenericTypeDefinition)
        {
            return false;
        }

        var types = GetGenericTypeImplementations(type, typeof(IDictionary<,>));
        return types.Any(
            t => t.GetGenericArguments()[0] == typeof(string)
                && t.GetGenericArguments()[1] == typeof(object));
    }

    public static Type MakeNullable(this Type type, bool nullable = true)
        => type.IsNullableType() == nullable
            ? type
            : nullable
                ? typeof(Nullable<>).MakeGenericType(type)
                : type.UnwrapNullableType();

    public static bool IsNumeric(this Type type)
    {
        type = type.UnwrapNullableType();

        return type.IsInteger()
            || type == typeof(decimal)
            || type == typeof(float)
            || type == typeof(double);
    }

    public static bool IsInteger(this Type type)
    {
        type = type.UnwrapNullableType();

        return type == typeof(int)
            || type == typeof(long)
            || type == typeof(short)
            || type == typeof(byte)
            || type == typeof(uint)
            || type == typeof(ulong)
            || type == typeof(ushort)
            || type == typeof(sbyte)
            || type == typeof(char);
    }

    public static bool IsSignedInteger(this Type type)
        => type == typeof(int)
            || type == typeof(long)
            || type == typeof(short)
            || type == typeof(sbyte);

    public static bool IsAnonymousType(this Type type)
        => type.Name.StartsWith("<>", StringComparison.Ordinal)
            && type.GetCustomAttributes(typeof(CompilerGeneratedAttribute), inherit: false).Length > 0
            && type.Name.Contains("AnonymousType");

    public static PropertyInfo? GetAnyProperty(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties)]
        this Type type,
        string name)
    {
        var props = type.GetRuntimeProperties().Where(p => p.Name == name).ToList();
        if (props.Count > 1)
        {
            throw new AmbiguousMatchException();
        }

        return props.SingleOrDefault();
    }

    public static bool IsInstantiable(this Type type)
        => !type.IsAbstract
            && !type.IsInterface
            && (!type.IsGenericType || !type.IsGenericTypeDefinition);

    public static Type UnwrapEnumType(this Type type)
    {
        var isNullable = type.IsNullableType();
        var underlyingNonNullableType = isNullable ? type.UnwrapNullableType() : type;
        if (!underlyingNonNullableType.IsEnum)
        {
            return type;
        }

        var underlyingEnumType = Enum.GetUnderlyingType(underlyingNonNullableType);
        return isNullable ? MakeNullable(underlyingEnumType) : underlyingEnumType;
    }

    public static Type GetSequenceType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type type)
    {
        var sequenceType = TryGetSequenceType(type);
        if (sequenceType == null)
        {
            throw new ArgumentException($"The type {type.Name} does not represent a sequence");
        }

        return sequenceType;
    }

    public static Type? TryGetSequenceType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type type)
        => type.TryGetElementType(typeof(IEnumerable<>))
            ?? type.TryGetElementType(typeof(IAsyncEnumerable<>));

    public static Type? TryGetElementType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type type,
        Type interfaceOrBaseType)
    {
        if (type.IsGenericTypeDefinition)
        {
            return null;
        }

        var types = GetGenericTypeImplementations(type, interfaceOrBaseType);

        Type? singleImplementation = null;
        foreach (var implementation in types)
        {
            if (singleImplementation == null)
            {
                singleImplementation = implementation;
            }
            else
            {
                singleImplementation = null;
                break;
            }
        }

        return singleImplementation?.GenericTypeArguments.FirstOrDefault();
    }

    public static bool IsCompatibleWith(this Type propertyType, Type fieldType)
    {
        if (propertyType.IsAssignableFrom(fieldType)
            || fieldType.IsAssignableFrom(propertyType))
        {
            return true;
        }

        var propertyElementType = propertyType.TryGetSequenceType();
        var fieldElementType = fieldType.TryGetSequenceType();

        return propertyElementType != null
            && fieldElementType != null
            && IsCompatibleWith(propertyElementType, fieldElementType);
    }

    public static IEnumerable<Type> GetGenericTypeImplementations(this Type type, Type interfaceOrBaseType)
    {
        var typeInfo = type.GetTypeInfo();
        if (!typeInfo.IsGenericTypeDefinition)
        {
            var baseTypes = interfaceOrBaseType.GetTypeInfo().IsInterface
                ? typeInfo.ImplementedInterfaces
                : type.GetBaseTypes();
            foreach (var baseType in baseTypes)
            {
                if (baseType.IsGenericType
                    && baseType.GetGenericTypeDefinition() == interfaceOrBaseType)
                {
                    yield return baseType;
                }
            }

            if (type.IsGenericType
                && type.GetGenericTypeDefinition() == interfaceOrBaseType)
            {
                yield return type;
            }
        }
    }

    public static IEnumerable<Type> GetBaseTypes(this Type type)
    {
        var currentType = type.BaseType;

        while (currentType != null)
        {
            yield return currentType;

            currentType = currentType.BaseType;
        }
    }

    public static List<Type> GetBaseTypesAndInterfacesInclusive(this Type type)
    {
        var baseTypes = new List<Type>();
        var typesToProcess = new Queue<Type>();
        typesToProcess.Enqueue(type);

        while (typesToProcess.Count > 0)
        {
            type = typesToProcess.Dequeue();
            baseTypes.Add(type);

            if (type.IsNullableValueType())
            {
                typesToProcess.Enqueue(Nullable.GetUnderlyingType(type)!);
            }

            if (type.IsConstructedGenericType)
            {
                typesToProcess.Enqueue(type.GetGenericTypeDefinition());
            }

            if (!type.IsGenericTypeDefinition
                && !type.IsInterface)
            {
                if (type.BaseType != null)
                {
                    typesToProcess.Enqueue(type.BaseType);
                }

                foreach (var @interface in GetDeclaredInterfaces(type))
                {
                    typesToProcess.Enqueue(@interface);
                }
            }
        }

        return baseTypes;
    }

    public static IEnumerable<Type> GetTypesInHierarchy(this Type type)
    {
        var currentType = type;

        while (currentType != null)
        {
            yield return currentType;

            currentType = currentType.BaseType;
        }
    }

    public static IEnumerable<Type> GetDeclaredInterfaces(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type type)
    {
        var interfaces = type.GetInterfaces();
        if (type.BaseType == typeof(object)
            || type.BaseType == null)
        {
            return interfaces;
        }

        return interfaces.Except(GetInterfacesSuppressed(type.BaseType));

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2070", Justification = "https://github.com/dotnet/linker/issues/2473")]
        static IEnumerable<Type> GetInterfacesSuppressed(Type type)
            => type.GetInterfaces();
    }

    public static ConstructorInfo? GetDeclaredConstructor(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        this Type type,
        Type[]? types)
    {
        types ??= Array.Empty<Type>();

        return type.GetTypeInfo().DeclaredConstructors
            .SingleOrDefault(
                c => !c.IsStatic
                    && c.GetParameters().Select(p => p.ParameterType).SequenceEqual(types))!;
    }

    public static IEnumerable<PropertyInfo> GetPropertiesInHierarchy(this Type type, string name)
    {
        var currentType = type;
        do
        {
            var typeInfo = currentType.GetTypeInfo();
            foreach (var propertyInfo in typeInfo.DeclaredProperties)
            {
                if (propertyInfo.Name.Equals(name, StringComparison.Ordinal)
                    && !(propertyInfo.GetMethod ?? propertyInfo.SetMethod)!.IsStatic)
                {
                    yield return propertyInfo;
                }
            }

            currentType = typeInfo.BaseType;
        }
        while (currentType != null);
    }

    // Looking up the members through the whole hierarchy allows to find inherited private members.
    public static IEnumerable<MemberInfo> GetMembersInHierarchy(this Type type)
    {
        var currentType = type;

        do
        {
            // Do the whole hierarchy for properties first since looking for fields is slower.
            foreach (var propertyInfo in currentType.GetRuntimeProperties().Where(pi => !(pi.GetMethod ?? pi.SetMethod)!.IsStatic))
            {
                yield return propertyInfo;
            }

            foreach (var fieldInfo in currentType.GetRuntimeFields().Where(f => !f.IsStatic))
            {
                yield return fieldInfo;
            }

            currentType = currentType.BaseType;
        }
        while (currentType != null);
    }

    public static IEnumerable<MemberInfo> GetMembersInHierarchy(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties
            | DynamicallyAccessedMemberTypes.NonPublicProperties
            | DynamicallyAccessedMemberTypes.PublicFields
            | DynamicallyAccessedMemberTypes.NonPublicFields)]
        this Type type,
        string name)
        => type.GetMembersInHierarchy().Where(m => m.Name == name);

    private static readonly Dictionary<Type, object> CommonTypeDictionary = new()
    {
#pragma warning disable IDE0034 // Simplify 'default' expression - default causes default(object)
        { typeof(int), default(int) },
        { typeof(Guid), default(Guid) },
        { typeof(DateOnly), default(DateOnly) },
        { typeof(DateTime), default(DateTime) },
        { typeof(DateTimeOffset), default(DateTimeOffset) },
        { typeof(TimeOnly), default(TimeOnly) },
        { typeof(long), default(long) },
        { typeof(bool), default(bool) },
        { typeof(double), default(double) },
        { typeof(short), default(short) },
        { typeof(float), default(float) },
        { typeof(byte), default(byte) },
        { typeof(char), default(char) },
        { typeof(uint), default(uint) },
        { typeof(ushort), default(ushort) },
        { typeof(ulong), default(ulong) },
        { typeof(sbyte), default(sbyte) }
#pragma warning restore IDE0034 // Simplify 'default' expression
    };

    public static object? GetDefaultValue(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] this Type type)
    {
        if (!type.IsValueType)
        {
            return null;
        }

        // A bit of perf code to avoid calling Activator.CreateInstance for common types and
        // to avoid boxing on every call. This is about 50% faster than just calling CreateInstance
        // for all value types.
        return CommonTypeDictionary.TryGetValue(type, out var value)
            ? value
            : Activator.CreateInstance(type);
    }

    [RequiresUnreferencedCode("Gets all types from the given assembly - unsafe for trimming")]
    public static IEnumerable<TypeInfo> GetConstructibleTypes(this Assembly assembly)
        => assembly.GetLoadableDefinedTypes().Where(
            t => !t.IsAbstract
                && !t.IsGenericTypeDefinition);

    [RequiresUnreferencedCode("Gets all types from the given assembly - unsafe for trimming")]
    public static IEnumerable<TypeInfo> GetLoadableDefinedTypes(this Assembly assembly)
    {
        try
        {
            return assembly.DefinedTypes;
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).Select(IntrospectionExtensions.GetTypeInfo!);
        }
    }

    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    public static string DisplayName(this Type type, bool fullName = true, bool compilable = false)
    {
        var stringBuilder = new StringBuilder();
        ProcessType(stringBuilder, type, fullName, compilable);
        return stringBuilder.ToString();
    }

    private static void ProcessType(StringBuilder builder, Type type, bool fullName, bool compilable)
    {
        if (type.IsGenericType)
        {
            var genericArguments = type.GetGenericArguments();
            ProcessGenericType(builder, type, genericArguments, genericArguments.Length, fullName, compilable);
        }
        else if (type.IsArray)
        {
            ProcessArrayType(builder, type, fullName, compilable);
        }
        else if (BuiltInTypeNames.TryGetValue(type, out var builtInName))
        {
            builder.Append(builtInName);
        }
        else if (!type.IsGenericParameter)
        {
            if (compilable)
            {
                if (type.IsNested)
                {
                    ProcessType(builder, type.DeclaringType!, fullName, compilable);
                    builder.Append('.');
                }
                else if (fullName)
                {
                    builder.Append(type.Namespace).Append('.');
                }

                builder.Append(type.Name);
            }
            else
            {
                builder.Append(fullName ? type.FullName : type.Name);
            }
        }
    }

    private static void ProcessArrayType(StringBuilder builder, Type type, bool fullName, bool compilable)
    {
        var innerType = type;
        while (innerType.IsArray)
        {
            innerType = innerType.GetElementType()!;
        }

        ProcessType(builder, innerType, fullName, compilable);

        while (type.IsArray)
        {
            builder.Append('[');
            builder.Append(',', type.GetArrayRank() - 1);
            builder.Append(']');
            type = type.GetElementType()!;
        }
    }

    private static void ProcessGenericType(
        StringBuilder builder,
        Type type,
        Type[] genericArguments,
        int length,
        bool fullName,
        bool compilable)
    {
        if (type.IsConstructedGenericType
            && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            ProcessType(builder, type.UnwrapNullableType(), fullName, compilable);
            builder.Append('?');
            return;
        }

        var offset = type.IsNested ? type.DeclaringType!.GetGenericArguments().Length : 0;

        if (compilable)
        {
            if (type.IsNested)
            {
                ProcessType(builder, type.DeclaringType!, fullName, compilable);
                builder.Append('.');
            }
            else if (fullName)
            {
                builder.Append(type.Namespace);
                builder.Append('.');
            }
        }
        else
        {
            if (fullName)
            {
                if (type.IsNested)
                {
                    ProcessGenericType(builder, type.DeclaringType!, genericArguments, offset, fullName, compilable);
                    builder.Append('+');
                }
                else
                {
                    builder.Append(type.Namespace);
                    builder.Append('.');
                }
            }
        }

        var genericPartIndex = type.Name.IndexOf('`');
        if (genericPartIndex <= 0)
        {
            builder.Append(type.Name);
            return;
        }

        builder.Append(type.Name, 0, genericPartIndex);
        builder.Append('<');

        for (var i = offset; i < length; i++)
        {
            ProcessType(builder, genericArguments[i], fullName, compilable);
            if (i + 1 == length)
            {
                continue;
            }

            builder.Append(',');
            if (!genericArguments[i + 1].IsGenericParameter)
            {
                builder.Append(' ');
            }
        }

        builder.Append('>');
    }

    public static IEnumerable<string> GetNamespaces(this Type type)
    {
        if (BuiltInTypeNames.ContainsKey(type))
        {
            yield break;
        }

        if (type.IsArray)
        {
            foreach (var ns in type.GetElementType()!.GetNamespaces())
            {
                yield return ns;
            }

            yield break;
        }

        yield return type.Namespace!;

        if (type.IsGenericType)
        {
            foreach (var typeArgument in type.GenericTypeArguments)
            {
                foreach (var ns in typeArgument.GetNamespaces())
                {
                    yield return ns;
                }
            }
        }
    }

    public static ConstantExpression GetDefaultValueConstant(this Type type)
        => (ConstantExpression)GenerateDefaultValueConstantMethod
            .MakeGenericMethod(type).Invoke(null, Array.Empty<object>())!;

    private static readonly MethodInfo GenerateDefaultValueConstantMethod =
        typeof(SharedTypeExtensions).GetTypeInfo().GetDeclaredMethod(nameof(GenerateDefaultValueConstant))!;

    private static ConstantExpression GenerateDefaultValueConstant<TDefault>()
        => Expression.Constant(default(TDefault), typeof(TDefault));
}

public class JsonQueryExpression : Expression, IPrintableExpression
{
    private readonly IReadOnlyDictionary<IProperty, ColumnExpression> _keyPropertyMap;

    /// <summary>
    ///     Creates a new instance of the <see cref="JsonQueryExpression" /> class.
    /// </summary>
    /// <param name="entityType">An entity type being represented by this expression.</param>
    /// <param name="jsonColumn">A column containing JSON value.</param>
    /// <param name="keyPropertyMap">A map of key properties and columns they map to in the database.</param>
    /// <param name="type">A type of the element represented by this expression.</param>
    /// <param name="collection">A value indicating whether this expression represents a collection or not.</param>
    public JsonQueryExpression(
        IEntityType entityType,
        ColumnExpression jsonColumn,
        IReadOnlyDictionary<IProperty, ColumnExpression> keyPropertyMap,
        Type type,
        bool collection)
        : this(
            entityType,
            jsonColumn,
            keyPropertyMap,
            path: new List<PathSegment> { new("$") },
            type,
            collection,
            jsonColumn.IsNullable)
    {
    }

    private JsonQueryExpression(
        IEntityType entityType,
        ColumnExpression jsonColumn,
        IReadOnlyDictionary<IProperty, ColumnExpression> keyPropertyMap,
        IReadOnlyList<PathSegment> path,
        Type type,
        bool collection,
        bool nullable)
    {
        Debug.Assert(entityType.FindPrimaryKey() != null, "primary key is null.");

        EntityType = entityType;
        JsonColumn = jsonColumn;
        IsCollection = collection;
        _keyPropertyMap = keyPropertyMap;
        Type = type;
        Path = path;
        IsNullable = nullable;
    }

    /// <summary>
    ///     The entity type being represented by this expression.
    /// </summary>
    public virtual IEntityType EntityType { get; }

    /// <summary>
    ///     The column containg JSON value.
    /// </summary>
    public virtual ColumnExpression JsonColumn { get; }

    /// <summary>
    ///     The value indicating whether this expression represents a collection.
    /// </summary>
    public virtual bool IsCollection { get; }

    /// <summary>
    ///     The list of path segments leading to the entity from the root of the JSON stored in the column.
    /// </summary>
    public virtual IReadOnlyList<PathSegment> Path { get; }

    /// <summary>
    ///     The value indicating whether this expression is nullable.
    /// </summary>
    public virtual bool IsNullable { get; }

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    public override Type Type { get; }

    /// <summary>
    ///     Binds a property with this JSON query expression to get the SQL representation.
    /// </summary>
    public virtual SqlExpression BindProperty(IProperty property)
    {
        if (!EntityType.IsAssignableFrom(property.DeclaringEntityType)
            && !property.DeclaringEntityType.IsAssignableFrom(EntityType))
        {
            throw new InvalidOperationException(
                RelationalStrings.UnableToBindMemberToEntityProjection("property", property.Name, EntityType.DisplayName()));
        }

        if (_keyPropertyMap.TryGetValue(property, out var match))
        {
            return match;
        }

        var newPath = Path.ToList();
        newPath.Add(new PathSegment(property.GetJsonPropertyName()!));

        return new JsonScalarExpression(
            JsonColumn,
            property,
            newPath,
            IsNullable || property.IsNullable);
    }

    /// <summary>
    ///     Binds a navigation with this JSON query expression to get the SQL representation.
    /// </summary>
    /// <param name="navigation">The navigation to bind.</param>
    /// <returns>An JSON query expression for the target entity type of the navigation.</returns>
    public virtual JsonQueryExpression BindNavigation(INavigation navigation)
    {
        if (navigation.ForeignKey.DependentToPrincipal == navigation)
        {
            // issue #28645
            throw new InvalidOperationException(
                @"RelationalStrings.JsonCantNavigateToParentEntity(
                    navigation.ForeignKey.DeclaringEntityType.DisplayName(),
                    navigation.ForeignKey.PrincipalEntityType.DisplayName(),
                    navigation.Name)");
        }

        var targetEntityType = navigation.TargetEntityType;
        var newPath = Path.ToList();
        newPath.Add(new PathSegment(targetEntityType.GetJsonPropertyName()!));

        var newKeyPropertyMap = new Dictionary<IProperty, ColumnExpression>();
        var targetPrimaryKeyProperties = targetEntityType.FindPrimaryKey()!.Properties.Take(_keyPropertyMap.Count);
        var sourcePrimaryKeyProperties = EntityType.FindPrimaryKey()!.Properties.Take(_keyPropertyMap.Count);
        foreach (var (target, source) in targetPrimaryKeyProperties.Zip(sourcePrimaryKeyProperties, (t, s) => (t, s)))
        {
            newKeyPropertyMap[target] = _keyPropertyMap[source];
        }

        return new JsonQueryExpression(
            targetEntityType,
            JsonColumn,
            newKeyPropertyMap,
            newPath,
            navigation.ClrType,
            navigation.IsCollection,
            IsNullable || !navigation.ForeignKey.IsRequiredDependent);
    }

    /// <summary>
    ///     Makes this JSON query expression nullable.
    /// </summary>
    /// <returns>A new expression which has <see cref="IsNullable" /> property set to true.</returns>
    public virtual JsonQueryExpression MakeNullable()
    {
        var keyPropertyMap = new Dictionary<IProperty, ColumnExpression>();
        foreach (var (property, columnExpression) in _keyPropertyMap)
        {
            keyPropertyMap[property] = columnExpression.MakeNullable();
        }

        return new JsonQueryExpression(
            EntityType,
            JsonColumn.MakeNullable(),
            keyPropertyMap,
            Path,
            Type,
            IsCollection,
            nullable: true);
    }

    /// <inheritdoc />
    public virtual void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("JsonQueryExpression(");
        expressionPrinter.Visit(JsonColumn);
        expressionPrinter.Append($", {string.Join("", Path.Select(e => e.ToString()))})");
    }

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var jsonColumn = (ColumnExpression)visitor.Visit(JsonColumn);
        var newKeyPropertyMap = new Dictionary<IProperty, ColumnExpression>();
        foreach (var (property, column) in _keyPropertyMap)
        {
            newKeyPropertyMap[property] = (ColumnExpression)visitor.Visit(column);
        }

        return Update(jsonColumn, newKeyPropertyMap);
    }

    /// <summary>
    ///     Creates a new expression that is like this one, but using the supplied children. If all of the children are the same, it will
    ///     return this expression.
    /// </summary>
    /// <param name="jsonColumn">The <see cref="JsonColumn" /> property of the result.</param>
    /// <param name="keyPropertyMap">The map of key properties and columns they map to.</param>
    /// <returns>This expression if no children changed, or an expression with the updated children.</returns>
    public virtual JsonQueryExpression Update(
        ColumnExpression jsonColumn,
        IReadOnlyDictionary<IProperty, ColumnExpression> keyPropertyMap)
        => jsonColumn != JsonColumn
            || keyPropertyMap.Count != _keyPropertyMap.Count
            || keyPropertyMap.Zip(_keyPropertyMap, (n, o) => n.Value != o.Value).Any(x => x)
                ? new JsonQueryExpression(EntityType, jsonColumn, keyPropertyMap, Path, Type, IsCollection, IsNullable)
                : this;

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
            && (ReferenceEquals(this, obj)
                || obj is JsonQueryExpression jsonQueryExpression
                && Equals(jsonQueryExpression));

    private bool Equals(JsonQueryExpression jsonQueryExpression)
        => EntityType.Equals(jsonQueryExpression.EntityType)
            && JsonColumn.Equals(jsonQueryExpression.JsonColumn)
            && IsCollection.Equals(jsonQueryExpression.IsCollection)
            && IsNullable == jsonQueryExpression.IsNullable
            && Path.SequenceEqual(jsonQueryExpression.Path)
            && KeyPropertyMapEquals(jsonQueryExpression._keyPropertyMap);

    private bool KeyPropertyMapEquals(IReadOnlyDictionary<IProperty, ColumnExpression> other)
    {
        if (_keyPropertyMap.Count != other.Count)
        {
            return false;
        }

        foreach (var (key, value) in _keyPropertyMap)
        {
            if (!other.TryGetValue(key, out var column) || !value.Equals(column))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
        // not incorporating _keyPropertyMap into the hash, too much work 
        => HashCode.Combine(EntityType, JsonColumn, IsCollection, Path, IsNullable);
}

public class PathSegment
{
    /// <summary>
    ///     Creates a new instance of the <see cref="PathSegment" /> class.
    /// </summary>
    /// <param name="key">A key which is being accessed in the JSON.</param>
    public PathSegment(string key)
    {
        Key = key;
    }

    /// <summary>
    ///     The key which is being accessed in the JSON.
    /// </summary>
    public virtual string Key { get; }

    /// <inheritdoc />
    public override string ToString()
        => (Key == "$" ? "" : ".") + Key;

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is PathSegment pathSegment
               && Equals(pathSegment));

    private bool Equals(PathSegment pathSegment)
        => Key == pathSegment.Key;

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Key);
}


public static class Extensions2
{
    public static string? GetJsonPropertyName(this IReadOnlyProperty property)
        => (string?)property.FindAnnotation("RelationalAnnotationNames.JsonPropertyName")?.Value
           ?? (property.IsKey() || !property.DeclaringEntityType.IsMappedToJson() ? null : property.Name);

    public static bool IsMappedToJson(this IReadOnlyEntityType entityType)
        => !string.IsNullOrEmpty(entityType.GetContainerColumnName());

    public static string? GetContainerColumnName(this IReadOnlyEntityType entityType)
        => entityType.FindAnnotation("RelationalAnnotationNames.ContainerColumnName")?.Value is string columnName
            ? columnName
            : (entityType.FindOwnership()?.PrincipalEntityType.GetContainerColumnName());

    public static string? GetJsonPropertyName(this IReadOnlyEntityType entityType)
        => (string?)entityType.FindAnnotation("RelationalAnnotationNames.JsonPropertyName")?.Value
           ?? (!entityType.IsMappedToJson() ? null : entityType.FindOwnership()!.GetNavigation(pointsToPrincipal: false)!.Name);
}


public class JsonScalarExpression
    : SqlExpression
{
    /// <summary>
    ///     Creates a new instance of the <see cref="JsonScalarExpression" /> class.
    /// </summary>
    /// <param name="jsonColumn">A column containg JSON value.</param>
    /// <param name="property">A property representing the result of this expression.</param>
    /// <param name="path">A list of path segments leading to the scalar from the root of the JSON stored in the column.</param>
    /// <param name="nullable">A value indicating whether the expression is nullable.</param>
    public JsonScalarExpression(
        ColumnExpression jsonColumn,
        IProperty property,
        IReadOnlyList<PathSegment> path,
        bool nullable)
        : this(jsonColumn, path, property.ClrType.UnwrapNullableType(), property.FindRelationalTypeMapping()!, nullable)
    {
    }

    internal JsonScalarExpression(
        ColumnExpression jsonColumn,
        IReadOnlyList<PathSegment> path,
        Type type,
        RelationalTypeMapping typeMapping,
        bool nullable)
        : base(type, typeMapping)
    {
        JsonColumn = jsonColumn;
        Path = path;
        IsNullable = nullable;
    }

    /// <summary>
    ///     The column containg JSON value.
    /// </summary>
    public virtual ColumnExpression JsonColumn { get; }

    /// <summary>
    ///     The list of path segments leading to the scalar from the root of the JSON stored in the column.
    /// </summary>
    public virtual IReadOnlyList<PathSegment> Path { get; }

    /// <summary>
    ///     The value indicating whether the expression is nullable.
    /// </summary>
    public virtual bool IsNullable { get; }

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var jsonColumn = (ColumnExpression)visitor.Visit(JsonColumn);
        var jsonColumnMadeNullable = jsonColumn.IsNullable && !JsonColumn.IsNullable;

        // TODO Call update: Issue#28887
        return jsonColumn != JsonColumn
            ? new JsonScalarExpression(
                jsonColumn,
                Path,
                Type,
                TypeMapping!,
                IsNullable || jsonColumnMadeNullable)
            : this;
    }

    /// <summary>
    ///     Creates a new expression that is like this one, but using the supplied children. If all of the children are the same, it will
    ///     return this expression.
    /// </summary>
    /// <param name="jsonColumn">The <see cref="JsonColumn" /> property of the result.</param>
    /// <returns>This expression if no children changed, or an expression with the updated children.</returns>
    public virtual JsonScalarExpression Update(ColumnExpression jsonColumn)
        => jsonColumn != JsonColumn
            ? new JsonScalarExpression(jsonColumn, Path, Type, TypeMapping!, IsNullable)
            : this;

    /// <inheritdoc />
    protected override void Print(ExpressionPrinter expressionPrinter)
    {
        expressionPrinter.Append("JsonScalarExpression(column: ");
        expressionPrinter.Visit(JsonColumn);
        expressionPrinter.Append($", {string.Join("", Path.Select(e => e.ToString()))})");
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is JsonScalarExpression jsonScalarExpression
            && JsonColumn.Equals(jsonScalarExpression.JsonColumn)
            && Path.SequenceEqual(jsonScalarExpression.Path);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), JsonColumn, Path);
}

public class EntityQueryRootExpression : QueryRootExpression, IPrintableExpression
{
    /// <summary>
    ///     Creates a new instance of the <see cref="EntityQueryRootExpression" /> class with associated query provider.
    /// </summary>
    /// <param name="asyncQueryProvider">The query provider associated with this query root.</param>
    /// <param name="entityType">The entity type this query root represents.</param>
    public EntityQueryRootExpression(IAsyncQueryProvider asyncQueryProvider, IEntityType entityType)
        : base(asyncQueryProvider, entityType)
    {
        EntityType = entityType;
    }

    /// <summary>
    ///     Creates a new instance of the <see cref="EntityQueryRootExpression" /> class without any query provider.
    /// </summary>
    /// <param name="entityType">The entity type this query root represents.</param>
    public EntityQueryRootExpression(IEntityType entityType)
        : base(entityType)
    {
        EntityType = entityType;
    }

    /// <summary>
    ///     The entity type represented by this query root.
    /// </summary>
    public override IEntityType EntityType { get; }

    /// <summary>
    ///     Detaches the associated query provider from this query root expression.
    /// </summary>
    /// <returns>A new query root expression without query provider.</returns>
    public override Expression DetachQueryProvider()
        => new EntityQueryRootExpression(EntityType);

    /// <summary>
    ///     Updates entity type associated with this query root with equivalent optimized version.
    /// </summary>
    /// <param name="entityType">The entity type to replace with.</param>
    /// <returns>New query root containing given entity type.</returns>
    public override EntityQueryRootExpression UpdateEntityType(IEntityType entityType)
        => entityType.ClrType != EntityType.ClrType
            || entityType.Name != EntityType.Name
                ? throw new InvalidOperationException(CoreStrings.QueryRootDifferentEntityType(entityType.DisplayName()))
                : new EntityQueryRootExpression(entityType);

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    public override bool CanReduce
        => false;

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => this;

    /// <summary>
    ///     Creates a printable string representation of the given expression using <see cref="ExpressionPrinter" />.
    /// </summary>
    /// <param name="expressionPrinter">The expression printer to use.</param>
    protected override void Print(ExpressionPrinter expressionPrinter)
        => expressionPrinter.Append(
            EntityType.HasSharedClrType
                ? $"DbSet<{EntityType.ClrType.ShortDisplayName()}>(\"{EntityType.Name}\")"
                : $"DbSet<{EntityType.ClrType.ShortDisplayName()}>()");

    /// <inheritdoc />
    void IPrintableExpression.Print(ExpressionPrinter expressionPrinter)
        => Print(expressionPrinter);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
            && (ReferenceEquals(this, obj)
                || obj is EntityQueryRootExpression queryRootExpression
                && EntityType == queryRootExpression.EntityType);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(base.GetHashCode(), EntityType);
}
