using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using EFCore.ReturningExtensions.NonQueryPatch;
using EFCore.ReturningExtensions.NonQueryPatch.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.SqlServer.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ReturningExtensions.NonQueryPatch.Infrastructure;

public class SqlServerQueryableMethodTranslatingExpressionVisitorFactoryPatch :
    SqlServerQueryableMethodTranslatingExpressionVisitorFactory
{
    private readonly IModel _model;

    public SqlServerQueryableMethodTranslatingExpressionVisitorFactoryPatch(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        RelationalQueryableMethodTranslatingExpressionVisitorDependencies relationalDependencies,
        IModel model)
        : base(dependencies, relationalDependencies)
    {
        _model = model;
    }

    public override QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
    {
        return new SqlServerQueryableMethodTranslatingExpressionVisitorPatch(
            Dependencies,
            RelationalDependencies,
            queryCompilationContext,
            _model);
    }
}

public class SqlServerQueryableMethodTranslatingExpressionVisitorPatch : 
    SqlServerQueryableMethodTranslatingExpressionVisitor
{
    private readonly IModel _model;
    private readonly RelationalSqlTranslatingExpressionVisitor _sqlTranslator;
    private readonly SharedTypeEntityExpandingExpressionVisitor _sharedTypeEntityExpandingExpressionVisitor;
    private readonly ISqlExpressionFactory _sqlExpressionFactory;

    public SqlServerQueryableMethodTranslatingExpressionVisitorPatch(
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

    protected SqlServerQueryableMethodTranslatingExpressionVisitorPatch(SqlServerQueryableMethodTranslatingExpressionVisitor parentVisitor) : base(parentVisitor)
    {
    }

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(RelationalQueryableExtensionsPatch))
        {
            var source = Visit(methodCallExpression.Arguments[0]);
            if (source is ShapedQueryExpression shapedQueryExpression)
            {
                var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
                switch (method.Name)
                {
                    case nameof(RelationalQueryableExtensionsPatch.ExecuteDelete)
                        when genericMethod == RelationalQueryableExtensionsPatch.ExecuteDeleteMethodInfoMarker:
                        return TranslateExecuteDelete(shapedQueryExpression)
                               ?? throw new ApplicationException();

                    case nameof(RelationalQueryableExtensionsPatch.ExecuteUpdate2)
                        when genericMethod == RelationalQueryableExtensionsPatch.ExecuteUpdateMethodInfoMarker:
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
        if (source.ShaperExpression is not EntityShaperExpression entityShaperExpression)
        {
            return null;
        }

        var selectExpression = (SelectExpression)source.QueryExpression;

        if (IsValidSelectExpressionForExecuteDelete(selectExpression, entityShaperExpression, out var tableExpression))
        {
            var entityType = entityShaperExpression.EntityType;
            if (AreOtherNonOwnedEntityTypesInTheTable(entityType.GetRootType(), tableExpression!.Table))
            {
                return null;
            }

            //selectExpression.ReplaceProjection(new List<Expression>());
            //selectExpression.ApplyProjection();

            return new NonQueryExpression(new DeleteExpression(tableExpression, selectExpression));

            static bool AreOtherNonOwnedEntityTypesInTheTable(IEntityType rootType, ITableBase table)
            {
                foreach (var entityTypeMapping in table.EntityTypeMappings)
                {
                    var entityType = entityTypeMapping.EntityType;
                    if (entityTypeMapping.IsSharedTablePrincipal == true
                        && entityType != rootType
                        || entityTypeMapping.IsSharedTablePrincipal == false
                        && entityType.GetRootType() != rootType
                        && !entityType.IsOwned())
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        return null; //base.TranslateExecuteDelete(source);
    }

    protected virtual bool IsValidSelectExpressionForExecuteDelete(
        SelectExpression selectExpression,
        EntityShaperExpression entityShaperExpression,
        out TableExpression? tableExpression)
    {
        if (selectExpression.Offset == null
            && selectExpression.Limit == null
            // If entity type has primary key then Distinct is no-op
            && (!selectExpression.IsDistinct || entityShaperExpression.EntityType.FindPrimaryKey() != null)
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0
            && selectExpression.Tables.Count >= 1
            // && selectExpression.Tables.Count == 1
            && selectExpression.Tables[0] is TableExpression expression)
        {
            tableExpression = expression;

            return true;
        }

        tableExpression = null;
        return false;
    }

    protected virtual bool IsValidSelectExpressionForExecuteUpdate(
        SelectExpression selectExpression,
        EntityShaperExpression entityShaperExpression,
        [NotNullWhen(true)] out TableExpression? tableExpression)
    {
        if (selectExpression.Offset == null
            // If entity type has primary key then Distinct is no-op
            && (!selectExpression.IsDistinct || entityShaperExpression.EntityType.FindPrimaryKey() != null)
            && selectExpression.GroupBy.Count == 0
            && selectExpression.Having == null
            && selectExpression.Orderings.Count == 0)
        {
            TableExpressionBase table;
            if (selectExpression.Tables.Count == 1)
            {
                table = selectExpression.Tables[0];
            }
            else
            {
                var projectionBindingExpression = (ProjectionBindingExpression)entityShaperExpression.ValueBufferExpression;
                var entityProjectionExpression = (EntityProjectionExpression)selectExpression.GetProjection(projectionBindingExpression);
                var column = entityProjectionExpression.BindProperty(entityShaperExpression.EntityType.GetProperties().First());
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

    protected virtual NonQueryExpression? TranslateExecuteUpdate(
        ShapedQueryExpression source,
        LambdaExpression setPropertyCalls)
    {
        var propertyValueLambdaExpressions = new List<(LambdaExpression, Expression)>();
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

        EntityShaperExpression? entityShaperExpression = null;
        var remappedUnwrappedLeftExpressions = new List<Expression>();
        foreach (var (propertyExpression, _) in propertyValueLambdaExpressions)
        {
            var left = RemapLambdaBody(source, propertyExpression);

            EntityShaperExpression? ese;

            if (!TryProcessPropertyAccess(_model, ref left, out ese))
            {
                AddTranslationErrorDetails("RelationalStrings.InvalidPropertyInSetProperty(propertyExpression.Print())");
                return null;
            }

            if (entityShaperExpression is null)
            {
                entityShaperExpression = ese;
            }
            else if (!ReferenceEquals(ese, entityShaperExpression))
            {
                AddTranslationErrorDetails(
                    @"RelationalStrings.MultipleEntityPropertiesInSetProperty(
                        entityShaperExpression.EntityType.DisplayName(), ese.EntityType.DisplayName())");
                return null;
            }

            remappedUnwrappedLeftExpressions.Add(left);
        }

        Debug.Assert(entityShaperExpression != null, "EntityShaperExpression should have a value.");

        var entityType = entityShaperExpression.EntityType;
#if NET7_0_OR_GREATER
        var mappingStrategy = entityType.GetMappingStrategy();
        if (mappingStrategy == RelationalAnnotationNames.TptMappingStrategy)
        {
            AddTranslationErrorDetails(
                RelationalStrings.ExecuteOperationOnTPT(nameof(RelationalQueryableExtensions.ExecuteUpdate), entityType.DisplayName()));
            return null;
        }

        if (mappingStrategy == RelationalAnnotationNames.TpcMappingStrategy
            && entityType.GetDirectlyDerivedTypes().Any())
        {
            // We allow TPC is it is leaf type
            AddTranslationErrorDetails(
                RelationalStrings.ExecuteOperationOnTPC(nameof(RelationalQueryableExtensions.ExecuteUpdate), entityType.DisplayName()));
            return null;
        }
#endif

        if (entityType.GetViewOrTableMappings().Count() != 1)
        {
            AddTranslationErrorDetails(
                @"RelationalStrings.ExecuteOperationOnEntitySplitting(
                    nameof(RelationalQueryableExtensions.ExecuteUpdate), entityType.DisplayName())");
            return null;
        }

        var selectExpression = (SelectExpression)source.QueryExpression;
        if (IsValidSelectExpressionForExecuteUpdate(selectExpression, entityShaperExpression, out var tableExpression))
        {
            return TranslateSetPropertyExpressions(
                this, source, selectExpression, tableExpression,
                propertyValueLambdaExpressions, remappedUnwrappedLeftExpressions, entityType);
        }

        // We need to convert to join with original query using PK
        var pk = entityType.FindPrimaryKey();
        if (pk == null)
        {
            AddTranslationErrorDetails(
                @"RelationalStrings.ExecuteOperationOnKeylessEntityTypeWithUnsupportedOperator(
                    nameof(RelationalQueryableExtensions.ExecuteUpdate),
                    entityType.DisplayName())");
            return null;
        }

        var outer = (ShapedQueryExpression)Visit(new EntityQueryRootExpression(entityType));
        var inner = source;
        var outerParameter = Expression.Parameter(entityType.ClrType);
        var outerKeySelector = Expression.Lambda(outerParameter.CreateKeyValuesExpression(pk.Properties), outerParameter);
        var firstPropertyLambdaExpression = propertyValueLambdaExpressions[0].Item1;
        var entitySource = GetEntitySource(_model, firstPropertyLambdaExpression.Body);
        var innerKeySelector = Expression.Lambda(
            entitySource.CreateKeyValuesExpression(pk.Properties), firstPropertyLambdaExpression.Parameters);

        var joinPredicate = CreateJoinPredicate(outer, outerKeySelector, inner, innerKeySelector);

        Debug.Assert(joinPredicate != null, "Join predicate shouldn't be null");

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

        return TranslateSetPropertyExpressions(this, outer, outerSelectExpression, tableExpression, propertyValueLambdaExpressions, null, entityType);

        static NonQueryExpression? TranslateSetPropertyExpressions(
            // RelationalQueryableMethodTranslatingExpressionVisitor visitor,
            SqlServerQueryableMethodTranslatingExpressionVisitorPatch visitor,
            ShapedQueryExpression source,
            SelectExpression selectExpression,
            TableExpression tableExpression,
            List<(LambdaExpression, Expression)> propertyValueLambdaExpressions,
            List<Expression>? leftExpressions,
            IEntityType entityType)
        {
            var columnValueSetters = new List<ColumnValueSetter>();
            for (var i = 0; i < propertyValueLambdaExpressions.Count; i++)
            {
                var (propertyExpression, valueExpression) = propertyValueLambdaExpressions[i];
                Expression left;
                if (leftExpressions != null)
                {
                    left = leftExpressions[i];
                }
                else
                {
                    left = visitor.RemapLambdaBody(source, propertyExpression);
                    left = left.UnwrapTypeConversion(out _);
                }

                var right = valueExpression is LambdaExpression lambdaExpression
                    ? visitor.RemapLambdaBody(source, lambdaExpression)
                    : valueExpression;

                if (right.Type != left.Type)
                {
                    right = Expression.Convert(right, left.Type);
                }

                // We generate equality between property = value while translating so that we infer the type mapping from property correctly.
                // Later we decompose it back into left/right components so that the equality is not in the tree which can get affected by
                // null semantics or other visitor.
                var setter = CreateEqualsExpression(left, right);
                var translation = visitor._sqlTranslator.Translate(setter);
                if (translation is SqlBinaryExpression
                    {
                        OperatorType: ExpressionType.Equal, Left: ColumnExpression column
                    } sqlBinaryExpression)
                {
                    columnValueSetters.Add(
                        new ColumnValueSetter(
                            column,
#if NET7_0_OR_GREATER
                            false //QuirkEnabled31078
                                ? sqlBinaryExpression.Right
                                : selectExpression.AssignUniqueAliases(sqlBinaryExpression.Right)
#else
                            sqlBinaryExpression.Right
#endif
                    ));
                }
                else
                {
                    // We would reach here only if the property is unmapped or value fails to translate.
                    visitor.AddTranslationErrorDetails(
                        @"RelationalStrings.UnableToTranslateSetProperty(
                            propertyExpression.Print(), valueExpression.Print(), visitor._sqlTranslator.TranslationErrorDetails)");
                    return null;
                }
            }

#if NET7_0_OR_GREATER
            selectExpression.ReplaceProjection(new List<Expression>());
            selectExpression.ApplyProjection();
#endif

            return new NonQueryExpression(new UpdateExpression(tableExpression, selectExpression, columnValueSetters, entityType));
        }

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

                case MethodCallExpression methodCallExpression
                    when methodCallExpression.Method.IsGenericMethod
                    && methodCallExpression.Method.Name == nameof(SetPropertyCalls<int>.SetProperty)
                    && methodCallExpression.Method.DeclaringType!.IsGenericType
                    && methodCallExpression.Method.DeclaringType.GetGenericTypeDefinition() == typeof(SetPropertyCalls<>):
                    list.Add(((LambdaExpression)methodCallExpression.Arguments[0], methodCallExpression.Arguments[1]));

                    PopulateSetPropertyCalls(methodCallExpression.Object!, list, parameter);

                    break;

                default:
                    AddTranslationErrorDetails("RelationalStrings.InvalidArgumentToExecuteUpdate");
                    break;
            }
        }

        // For property setter selectors in ExecuteUpdate, we support only simple member access, EF.Function, etc.
        // We also unwrap casts to interface/base class (#29618). Note that owned IncludeExpressions have already been pruned from the
        // source before remapping the lambda (#28727).
        static bool TryProcessPropertyAccess(
            IModel model,
            ref Expression expression,
            [NotNullWhen(true)] out EntityShaperExpression? entityShaperExpression)
        {
            expression = expression.UnwrapTypeConversion(out _);

            if (expression is MemberExpression { Expression: not null } memberExpression
                && Unwrap(memberExpression.Expression) is EntityShaperExpression ese)
            {
                expression = memberExpression.Update(ese);
                entityShaperExpression = ese;
                return true;
            }

            if (expression is MethodCallExpression mce)
            {
                if (mce.TryGetEFPropertyArguments(out var source, out _)
                    && Unwrap(source) is EntityShaperExpression ese1)
                {
                    if (source != ese1)
                    {
                        var rewrittenArguments = mce.Arguments.ToArray();
                        rewrittenArguments[0] = ese1;
                        expression = mce.Update(mce.Object, rewrittenArguments);
                    }

                    entityShaperExpression = ese1;
                    return true;
                }

                if (mce.TryGetIndexerArguments(model, out var source2, out _)
                    && Unwrap(source2) is EntityShaperExpression ese2)
                {
                    expression = mce.Update(ese2, mce.Arguments);
                    entityShaperExpression = ese2;
                    return true;
                }
            }

            entityShaperExpression = null;
            return false;

            static Expression Unwrap(Expression expression)
            {
                expression = expression.UnwrapTypeConversion(out _);

                return expression;
            }
        }

        // Old quirked implementation only
        static bool IsValidPropertyAccess(
            IModel model,
            Expression expression,
            [NotNullWhen(true)] out EntityShaperExpression? entityShaperExpression)
        {
            if (expression is MemberExpression { Expression: EntityShaperExpression ese })
            {
                entityShaperExpression = ese;
                return true;
            }

            if (expression is MethodCallExpression mce)
            {
                if (mce.TryGetEFPropertyArguments(out var source, out _)
                    && source is EntityShaperExpression ese1)
                {
                    entityShaperExpression = ese1;
                    return true;
                }

                if (mce.TryGetIndexerArguments(model, out var source2, out _)
                    && source2 is EntityShaperExpression ese2)
                {
                    entityShaperExpression = ese2;
                    return true;
                }
            }

            entityShaperExpression = null;
            return false;
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
        if (translation == null && _sqlTranslator.TranslationErrorDetails != null)
        {
            AddTranslationErrorDetails(_sqlTranslator.TranslationErrorDetails);
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

public class ColumnValueSetter
{
    /// <summary>
    ///     Creates a new instance of the <see cref="ColumnValueSetter" /> class.
    /// </summary>
    /// <param name="column">A column to be updated.</param>
    /// <param name="value">A value to be assigned to the column.</param>
    public ColumnValueSetter(ColumnExpression column, SqlExpression value)
    {
        Column = column;
        Value = value;
    }

    /// <summary>
    ///     The column to update value of.
    /// </summary>
    public virtual ColumnExpression Column { get; }

    /// <summary>
    ///     The value to be assigned to the column.
    /// </summary>
    public virtual SqlExpression Value { get; }

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
           && (ReferenceEquals(this, obj)
               || obj is ColumnValueSetter columnValueSetter
               && Equals(columnValueSetter));

    private bool Equals(ColumnValueSetter columnValueSetter)
        => Column == columnValueSetter.Column
           && Value == columnValueSetter.Value;

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Column, Value);
}

public sealed class SetPropertyCalls<TSource>
{
    private SetPropertyCalls()
    {
    }

    /// <summary>
    ///     Specifies a property and corresponding value it should be updated to in ExecuteUpdate method.
    /// </summary>
    /// <typeparam name="TProperty">The type of property.</typeparam>
    /// <param name="propertyExpression">A property access expression.</param>
    /// <param name="valueExpression">A value expression.</param>
    /// <returns>
    ///     The same instance so that multiple calls to
    ///     <see cref="SetPropertyCalls{TSource}.SetProperty{TProperty}(Func{TSource, TProperty}, Func{TSource, TProperty})" />
    ///     can be chained.
    /// </returns>
    public SetPropertyCalls<TSource> SetProperty<TProperty>(
        Func<TSource, TProperty> propertyExpression,
        Func<TSource, TProperty> valueExpression)
        => throw new InvalidOperationException("RelationalStrings.SetPropertyMethodInvoked");

    /// <summary>
    ///     Specifies a property and corresponding value it should be updated to in ExecuteUpdate method.
    /// </summary>
    /// <typeparam name="TProperty">The type of property.</typeparam>
    /// <param name="propertyExpression">A property access expression.</param>
    /// <param name="valueExpression">A value expression.</param>
    /// <returns>
    ///     The same instance so that multiple calls to
    ///     <see cref="SetPropertyCalls{TSource}.SetProperty{TProperty}(Func{TSource, TProperty}, TProperty)" /> can be chained.
    /// </returns>
    public SetPropertyCalls<TSource> SetProperty<TProperty>(
        Func<TSource, TProperty> propertyExpression,
        TProperty valueExpression)
        => throw new InvalidOperationException("RelationalStrings.SetPropertyMethodInvoked");

    #region Hidden System.Object members

    /// <summary>
    ///     Returns a string that represents the current object.
    /// </summary>
    /// <returns>A string that represents the current object.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override string? ToString()
        => base.ToString();

    /// <summary>
    ///     Determines whether the specified object is equal to the current object.
    /// </summary>
    /// <param name="obj">The object to compare with the current object.</param>
    /// <returns><see langword="true" /> if the specified object is equal to the current object; otherwise, <see langword="false" />.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    // ReSharper disable once BaseObjectEqualsIsObjectEquals
    public override bool Equals(object? obj)
        => base.Equals(obj);

    /// <summary>
    ///     Serves as the default hash function.
    /// </summary>
    /// <returns>A hash code for the current object.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    // ReSharper disable once BaseObjectGetHashCodeCallInGetHashCode
    public override int GetHashCode()
        => base.GetHashCode();

    #endregion
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
