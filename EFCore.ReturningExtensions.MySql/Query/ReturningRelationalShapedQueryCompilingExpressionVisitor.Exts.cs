// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Modified by Gordeev Dmitry in 2026 for EFCore.ReturningExtensions.
// Original source: Entity Framework Core (https://github.com/dotnet/efcore), licensed under the MIT License.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ReturningExtensions.MySql.Query;

public static class ExpressionExtensions1
{
    /// <summary>
    ///     Creates a printable string representation of the given expression.
    /// </summary>
    /// <param name="expression">The expression.</param>
    /// <param name="characterLimit">An optional limit to the number of characters included. Additional output will be truncated.</param>
    /// <returns>The printable representation.</returns>
    public static string Print(this Expression expression, int? characterLimit = null)
        => new ExpressionPrinter().PrintExpression(expression, characterLimit);

    /// <summary>
    ///     Creates a <see cref="MemberExpression"></see> that represents accessing either a field or a property.
    /// </summary>
    /// <param name="expression">An <see cref="Expression"></see> that represents the object that the member belongs to.</param>
    /// <param name="member">The <see cref="MemberInfo"></see> that describes the field or property to be accessed.</param>
    /// <returns>The <see cref="MemberExpression"></see> that results from calling the appropriate factory method.</returns>
    public static MemberExpression MakeMemberAccess(
        this Expression? expression,
        MemberInfo member)
    {
        var memberDeclaringClrType = member.DeclaringType;
        if (expression != null
            && memberDeclaringClrType != expression.Type
            && expression.Type.IsAssignableFrom(memberDeclaringClrType))
        {
            expression = Expression.Convert(expression, memberDeclaringClrType);
        }

        return Expression.MakeMemberAccess(expression, member);
    }

    /// <summary>
    ///     Creates a <see cref="BinaryExpression"></see> that represents an assignment operation.
    /// </summary>
    /// <param name="memberExpression">The member to which assignment will be made.</param>
    /// <param name="valueExpression">The value that will be assigned.</param>
    /// <returns>The <see cref="BinaryExpression" /> representing the assignment binding.</returns>
    [UnconditionalSuppressMessage(
        "ReflectionAnalysis", "IL2077",
        Justification = "AssignBinaryExpression is preserved via DynamicDependency below")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "System.Linq.Expressions.AssignBinaryExpression", "System.Linq.Expressions")]
    public static Expression Assign(
        this MemberExpression memberExpression,
        Expression valueExpression)
    {
        if (memberExpression.Member is FieldInfo { IsInitOnly: true })
        {
            return (BinaryExpression)Activator.CreateInstance(
                GetAssignBinaryExpressionType(),
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new object[] { memberExpression, valueExpression },
                null)!;
        }

        return Expression.Assign(memberExpression, valueExpression);

        [UnconditionalSuppressMessage(
            "ReflectionAnalysis", "IL2026",
            Justification = "DynamicDependency ensures AssignBinaryExpression isn't trimmed")]
        static Type GetAssignBinaryExpressionType()
            => typeof(Expression).Assembly.GetType("System.Linq.Expressions.AssignBinaryExpression", throwOnError: true)!;
    }

    /// <summary>
    ///     If the given a method-call expression represents a call to <see cref="EF.Property{TProperty}" />, then this
    ///     method extracts the entity expression and property name.
    /// </summary>
    /// <param name="methodCallExpression">The method-call expression for <see cref="EF.Property{TProperty}" /></param>
    /// <param name="entityExpression">The extracted entity access expression.</param>
    /// <param name="propertyName">The accessed property name.</param>
    /// <returns><see langword="true" /> if the method-call was for <see cref="EF.Property{TProperty}" />; <see langword="false" /> otherwise.</returns>
    public static bool TryGetEFPropertyArguments(
        this MethodCallExpression methodCallExpression,
        [NotNullWhen(true)] out Expression? entityExpression,
        [NotNullWhen(true)] out string? propertyName)
    {
        if (methodCallExpression.Method.IsEFPropertyMethod()
            && methodCallExpression.Arguments[1] is ConstantExpression propertyNameExpression)
        {
            entityExpression = methodCallExpression.Arguments[0];
            propertyName = (string)propertyNameExpression.Value!;
            return true;
        }

        (entityExpression, propertyName) = (null, null);
        return false;
    }

    /// <summary>
    ///     If the given a method-call expression represents a call to indexer on the entity, then this
    ///     method extracts the entity expression and property name.
    /// </summary>
    /// <param name="methodCallExpression">The method-call expression for indexer.</param>
    /// <param name="model">The model to use.</param>
    /// <param name="entityExpression">The extracted entity access expression.</param>
    /// <param name="propertyName">The accessed property name.</param>
    /// <returns><see langword="true" /> if the method-call was for indexer; <see langword="false" /> otherwise.</returns>
    public static bool TryGetIndexerArguments(
        this MethodCallExpression methodCallExpression,
        IModel model,
        [NotNullWhen(true)] out Expression? entityExpression,
        [NotNullWhen(true)] out string? propertyName)
    {
        if (model.IsIndexerMethod(methodCallExpression.Method)
            && methodCallExpression.Arguments[0] is ConstantExpression propertyNameExpression)
        {
            entityExpression = methodCallExpression.Object!;
            propertyName = (string)propertyNameExpression.Value!;

            return true;
        }

        (entityExpression, propertyName) = (null, null);
        return false;
    }

    /// <summary>
    ///     Gets the <see cref="PropertyInfo" /> represented by a simple property-access expression.
    /// </summary>
    /// <remarks>
    ///     This method is typically used to parse property access lambdas from fluent APIs.
    /// </remarks>
    /// <param name="propertyAccessExpression">The expression.</param>
    /// <returns>The <see cref="PropertyInfo" />.</returns>
    public static PropertyInfo GetPropertyAccess(this LambdaExpression propertyAccessExpression)
        => GetInternalMemberAccess<PropertyInfo>(propertyAccessExpression);

    /// <summary>
    ///     Gets the <see cref="MemberInfo" /> represented by a simple member-access expression.
    /// </summary>
    /// <remarks>
    ///     This method is typically used to parse member access lambdas from fluent APIs.
    /// </remarks>
    /// <param name="memberAccessExpression">The expression.</param>
    /// <returns>The <see cref="MemberInfo" />.</returns>
    public static MemberInfo GetMemberAccess(this LambdaExpression memberAccessExpression)
        => GetInternalMemberAccess<MemberInfo>(memberAccessExpression);

    private static TMemberInfo GetInternalMemberAccess<TMemberInfo>(this LambdaExpression memberAccessExpression)
        where TMemberInfo : MemberInfo
    {
        var parameterExpression = memberAccessExpression.Parameters[0];
        var memberInfo = parameterExpression.MatchSimpleMemberAccess<TMemberInfo>(memberAccessExpression.Body);

        if (memberInfo == null)
        {
            throw new ArgumentException(
                CoreStrings.InvalidMemberExpression(memberAccessExpression),
                nameof(memberAccessExpression));
        }

        var declaringType = memberInfo.DeclaringType;
        var parameterType = parameterExpression.Type;

        if (declaringType != null
            && declaringType != parameterType
            && declaringType.IsInterface
            && declaringType.IsAssignableFrom(parameterType)
            && memberInfo is PropertyInfo propertyInfo)
        {
            var propertyGetter = propertyInfo.GetMethod;
            var interfaceMapping = parameterType.GetTypeInfo().GetRuntimeInterfaceMap(declaringType);
            var index = Array.FindIndex(interfaceMapping.InterfaceMethods, p => p.Equals(propertyGetter));
            var targetMethod = interfaceMapping.TargetMethods[index];
            foreach (var runtimeProperty in parameterType.GetRuntimeProperties())
            {
                if (targetMethod.Equals(runtimeProperty.GetMethod))
                {
                    return (TMemberInfo)(object)runtimeProperty;
                }
            }
        }

        return memberInfo;
    }

    /// <summary>
    ///     Returns a list of <see cref="PropertyInfo" /> extracted from the given simple
    ///     <see cref="LambdaExpression" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only simple expressions are supported, such as those used to reference a property.
    ///     </para>
    ///     <para>
    ///         This method is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </remarks>
    /// <param name="propertyAccessExpression">The expression.</param>
    /// <returns>The list of referenced properties.</returns>
    public static IReadOnlyList<PropertyInfo> GetPropertyAccessList(this LambdaExpression propertyAccessExpression)
    {
        if (propertyAccessExpression.Parameters.Count != 1)
        {
            throw new ArgumentException(
                CoreStrings.InvalidMembersExpression(propertyAccessExpression),
                nameof(propertyAccessExpression));
        }

        var propertyPaths = propertyAccessExpression
            .MatchMemberAccessList((p, e) => e.MatchSimpleMemberAccess<PropertyInfo>(p));

        if (propertyPaths == null)
        {
            throw new ArgumentException(
                CoreStrings.InvalidMembersExpression(propertyAccessExpression),
                nameof(propertyAccessExpression));
        }

        return propertyPaths;
    }

    /// <summary>
    ///     Returns a list of <see cref="MemberInfo" /> extracted from the given simple
    ///     <see cref="LambdaExpression" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only simple expressions are supported, such as those used to reference a member.
    ///     </para>
    ///     <para>
    ///         This method is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </remarks>
    /// <param name="memberAccessExpression">The expression.</param>
    /// <returns>The list of referenced members.</returns>
    public static IReadOnlyList<MemberInfo> GetMemberAccessList(this LambdaExpression memberAccessExpression)
    {
        var memberPaths = memberAccessExpression
            .MatchMemberAccessList((p, e) => e.MatchSimpleMemberAccess<MemberInfo>(p));

        if (memberPaths == null)
        {
            throw new ArgumentException(
                CoreStrings.InvalidMembersExpression(memberAccessExpression),
                nameof(memberAccessExpression));
        }

        return memberPaths;
    }

    /// <summary>
    ///     <para>
    ///         Creates an <see cref="Expression" /> tree representing reading a value from a <see cref="ValueBuffer" />
    ///     </para>
    ///     <para>
    ///         This method is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </summary>
    /// <param name="valueBuffer">The expression that exposes the <see cref="ValueBuffer" />.</param>
    /// <param name="type">The type to read.</param>
    /// <param name="index">The index in the buffer to read from.</param>
    /// <param name="property">The IPropertyBase being read if any.</param>
    /// <returns>An expression to read the value.</returns>
    public static Expression CreateValueBufferReadValueExpression(
        this Expression valueBuffer,
        Type type,
        int index,
        IPropertyBase? property)
        => property is INavigationBase
            ? Expression.Constant(null, typeof(object))
            : Expression.Call(
                MakeValueBufferTryReadValueMethod(type),
                valueBuffer,
                Expression.Constant(index),
                Expression.Constant(property, typeof(IPropertyBase)));

    /// <summary>
    ///     <para>
    ///         MethodInfo which is used to generate an <see cref="Expression" /> tree representing reading a value from a
    ///         <see cref="ValueBuffer" />
    ///     </para>
    ///     <para>
    ///         This method is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </summary>
    public static readonly MethodInfo ValueBufferTryReadValueMethod
        = typeof(ExpressionExtensions1).GetTypeInfo().GetDeclaredMethod(nameof(ValueBufferTryReadValue))!;

    [UnconditionalSuppressMessage(
        "ReflectionAnalysis", "IL2060",
        Justification = "ValueBufferTryReadValueMethod has no DynamicallyAccessedMembers annotations and is safe to construct.")]
    private static MethodInfo MakeValueBufferTryReadValueMethod(Type type)
        => ValueBufferTryReadValueMethod.MakeGenericMethod(type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TValue ValueBufferTryReadValue<TValue>(
#pragma warning disable IDE0060 // Remove unused parameter
        in ValueBuffer valueBuffer,
        int index,
        IPropertyBase property)
#pragma warning restore IDE0060 // Remove unused parameter
        => (TValue)valueBuffer[index]!;

    /// <summary>
    ///     <para>
    ///         Creates an <see cref="Expression" /> tree representing reading of a key values on given expression.
    ///     </para>
    ///     <para>
    ///         This method is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </summary>
    /// <param name="target">The expression that will be root for generated read operation.</param>
    /// <param name="properties">The list of properties to use to generate key values.</param>
    /// <param name="makeNullable">A value indicating if the key values should be read nullable.</param>
    /// <returns>An expression to read the key values.</returns>
    public static Expression CreateKeyValuesExpression(
        this Expression target,
        IReadOnlyList<IProperty> properties,
        bool makeNullable = false)
        => properties.Count == 1
            ? target.CreateEFPropertyExpression(properties[0], makeNullable)
            : Expression.NewArrayInit(
                typeof(object),
                properties
                    .Select(p => Expression.Convert(target.CreateEFPropertyExpression(p, makeNullable), typeof(object))));

    /// <summary>
    ///     <para>
    ///         Creates an <see cref="Expression" /> tree representing EF property access on given expression.
    ///     </para>
    ///     <para>
    ///         This method is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </summary>
    /// <param name="target">The expression that will be root for generated read operation.</param>
    /// <param name="property">The property to access.</param>
    /// <param name="makeNullable">A value indicating if the value can be nullable.</param>
    /// <returns>An expression to access EF property on given expression.</returns>
    public static Expression CreateEFPropertyExpression(
        this Expression target,
        IPropertyBase property,
        bool makeNullable = true) // No shadow entities in runtime
        => CreateEFPropertyExpression(target, property.DeclaringType.ClrType, property.ClrType, property.Name, makeNullable);

    private static Expression CreateEFPropertyExpression(
        Expression target,
        Type propertyDeclaringType,
        Type propertyType,
        string propertyName,
        bool makeNullable)
    {
        if (propertyDeclaringType != target.Type
            && target.Type.IsAssignableFrom(propertyDeclaringType))
        {
            target = Expression.Convert(target, propertyDeclaringType);
        }

        if (makeNullable)
        {
            propertyType = propertyType.MakeNullable();
        }

        // EF.Property expects an object as its first argument. If the target is a struct (complex type), we need an explicit up-cast to
        // object.
        if (target.Type.IsValueType)
        {
            target = Expression.Convert(target, typeof(object));
        }

        return Expression.Call(
            EF2.MakePropertyMethod(propertyType),
            target,
            Expression.Constant(propertyName));
    }

    private static readonly MethodInfo ObjectEqualsMethodInfo
        = typeof(object).GetRuntimeMethod(nameof(object.Equals), new[] { typeof(object), typeof(object) })!;

    /// <summary>
    ///     <para>
    ///         Creates an <see cref="Expression" /> tree representing equality comparison between 2 expressions using
    ///         <see cref="object.Equals(object?, object?)" /> method.
    ///     </para>
    ///     <para>
    ///         This method is typically used by database providers (and other extensions). It is generally
    ///         not used in application code.
    ///     </para>
    /// </summary>
    /// <param name="left">The left expression in equality comparison.</param>
    /// <param name="right">The right expression in equality comparison.</param>
    /// <param name="negated">If the comparison is non-equality.</param>
    /// <returns>An expression to compare left and right expressions.</returns>
    public static Expression CreateEqualsExpression(
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
}

public static class EF2
{
    internal static readonly MethodInfo PropertyMethod
        = typeof(EF).GetTypeInfo().GetDeclaredMethod(nameof(EF.Property))!;

    [UnconditionalSuppressMessage(
        "ReflectionAnalysis", "IL2060",
        Justification = "EF.Property has no DynamicallyAccessedMembers annotations and is safe to construct.")]
    internal static MethodInfo MakePropertyMethod(Type type)
        => PropertyMethod.MakeGenericMethod(type);
}

internal static class ExpressionExtensions2
{
    public static bool IsNullConstantExpression(this Expression expression)
        => RemoveConvert(expression) is ConstantExpression { Value: null };

    public static LambdaExpression UnwrapLambdaFromQuote(this Expression expression)
        => (LambdaExpression)(expression is UnaryExpression unary && expression.NodeType == ExpressionType.Quote
            ? unary.Operand
            : expression);

    [return: NotNullIfNotNull("expression")]
    public static Expression? UnwrapTypeConversion(this Expression? expression, out Type? convertedType)
    {
        convertedType = null;
        while (expression is UnaryExpression
               {
                   NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs
               } unaryExpression)
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
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression
            ? RemoveConvert(unaryExpression.Operand)
            : expression;

    public static T GetConstantValue<T>(this Expression expression)
    {
        return expression switch
        {
            StructuralTypeProjectionExpression projection => (T)(IReadOnlyDictionary<IProperty, ColumnExpression>)typeof(StructuralTypeProjectionExpression).GetField("_propertyExpressionMap", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.GetField)!.GetValue(projection)!,
            ConstantExpression constantExpression => (T)constantExpression.Value!,
            _ => throw new InvalidOperationException()
        };
    }
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
        => type is { IsClass: true, IsArray: false }
            && type != typeof(string);

    public static bool IsValidComplexType(this Type type)
        => !type.IsArray
            && !type.IsInterface
            && !IsScalarType(type);

    public static bool IsScalarType(this Type type)
        => type == typeof(string)
            || CommonTypeDictionary.ContainsKey(type);

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
        => type is { IsAbstract: false, IsInterface: false }
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

            if (type is { IsGenericTypeDefinition: false, IsInterface: false })
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
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
        this Type type)
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
            t => t is { IsAbstract: false, IsGenericTypeDefinition: false });

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

internal static class EnumerableExtensions3
{
    public static IOrderedEnumerable<TSource> OrderByOrdinal<TSource>(
        this IEnumerable<TSource> source,
        Func<TSource, string> keySelector)
        => source.OrderBy(keySelector, StringComparer.Ordinal);

    public static IEnumerable<T> Distinct<T>(
        this IEnumerable<T> source,
        Func<T?, T?, bool> comparer)
        where T : class
        => source.Distinct(new DynamicEqualityComparer<T>(comparer));

    private sealed class DynamicEqualityComparer<T> : IEqualityComparer<T>
        where T : class
    {
        private readonly Func<T?, T?, bool> _func;

        public DynamicEqualityComparer(Func<T?, T?, bool> func)
        {
            _func = func;
        }

        public bool Equals(T? x, T? y)
            => _func(x, y);

        public int GetHashCode(T obj)
            => 0;
    }

    public static string Join(
        this IEnumerable<object> source,
        string separator = ", ")
        => string.Join(separator, source);

    public static bool StructuralSequenceEqual<TSource>(
        this IEnumerable<TSource> first,
        IEnumerable<TSource> second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        using var firstEnumerator = first.GetEnumerator();
        using var secondEnumerator = second.GetEnumerator();
        while (firstEnumerator.MoveNext())
        {
            if (!secondEnumerator.MoveNext()
                || !StructuralComparisons.StructuralEqualityComparer
                    .Equals(firstEnumerator.Current, secondEnumerator.Current))
            {
                return false;
            }
        }

        return !secondEnumerator.MoveNext();
    }

    public static bool StartsWith<TSource>(
        this IEnumerable<TSource> first,
        IEnumerable<TSource> second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        using var firstEnumerator = first.GetEnumerator();
        using var secondEnumerator = second.GetEnumerator();

        while (secondEnumerator.MoveNext())
        {
            if (!firstEnumerator.MoveNext()
                || !Equals(firstEnumerator.Current, secondEnumerator.Current))
            {
                return false;
            }
        }

        return true;
    }

    public static int IndexOf<T>(this IEnumerable<T> source, T item)
        => IndexOf(source, item, EqualityComparer<T>.Default);

    public static int IndexOf<T>(
        this IEnumerable<T> source,
        T item,
        IEqualityComparer<T> comparer)
        => source.Select(
                (x, index) =>
                    comparer.Equals(item, x) ? index : -1)
            .FirstOr(x => x != -1, -1);

    public static T FirstOr<T>(this IEnumerable<T> source, T alternate)
        => source.DefaultIfEmpty(alternate).First();

    public static T FirstOr<T>(this IEnumerable<T> source, Func<T, bool> predicate, T alternate)
        => source.Where(predicate).FirstOr(alternate);

    public static bool Any(this IEnumerable source)
    {
        foreach (var _ in source)
        {
            return true;
        }

        return false;
    }

    public static async Task<List<TSource>> ToListAsync<TSource>(
        this IAsyncEnumerable<TSource> source,
        CancellationToken cancellationToken = default)
    {
        var list = new List<TSource>();
        await foreach (var element in source.WithCancellation(cancellationToken))
        {
            list.Add(element);
        }

        return list;
    }

    public static List<TSource> ToList<TSource>(this IEnumerable source)
        => source.OfType<TSource>().ToList();

    public static string Format(this IEnumerable<string> strings)
        => "{"
            + string.Join(
                ", ",
                strings.Select(s => "'" + s + "'"))
            + "}";
}