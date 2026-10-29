using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace EFCore.ReturningExtensions.SqlServer.Query;

internal static class ExpressionExtensions
{
    private static Expression RemoveConvert(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression
            ? RemoveConvert(unaryExpression.Operand)
            : expression;

    public static T GetConstantValue<T>(this Expression expression)
        => RemoveConvert(expression) switch
        {
            StructuralTypeProjectionExpression projection => (T)(IReadOnlyDictionary<IProperty, ColumnExpression>)typeof(StructuralTypeProjectionExpression).GetField("_propertyExpressionMap", BindingFlags.NonPublic| BindingFlags.Instance | BindingFlags.GetField)!.GetValue(projection)!,
            ConstantExpression constantExpression => (T)constantExpression.Value!,
#pragma warning disable EF9100 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            LiftableConstantExpression liftableConstantExpression => (T)liftableConstantExpression.OriginalExpression.Value!,
#pragma warning restore EF9100 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            _ => throw new InvalidOperationException()
        };

    public static bool TryGetNonNullConstantValue<T>(this Expression expression, [NotNullWhen(true), MaybeNullWhen(false)] out T value)
    {
        switch (expression)
        {
            case ConstantExpression { Value: T typedValue }:
                value = typedValue;
                return true;
#pragma warning disable EF9100 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            case LiftableConstantExpression { OriginalExpression.Value: T typedValue }:
#pragma warning restore EF9100 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                value = typedValue;
                return true;
            default:
                value = default;
                return false;
        }
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

    public static Type MakeNullable(this Type type, bool nullable = true)
        => type.IsNullableType() == nullable
            ? type
            : nullable
                ? typeof(Nullable<>).MakeGenericType(type)
                : type.UnwrapNullableType();

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
        else if (compilable)
        {
            builder.Append(type.Name);
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

        var offset = type.DeclaringType != null ? type.DeclaringType.GetGenericArguments().Length : 0;

        if (compilable)
        {
            if (type.DeclaringType != null)
            {
                ProcessGenericType(builder, type.DeclaringType, genericArguments, offset, fullName, compilable);
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
                if (type.DeclaringType != null)
                {
                    ProcessGenericType(builder, type.DeclaringType, genericArguments, offset, fullName, compilable);
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

        if (type.IsConstructedGenericType
            && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            foreach (var ns in type.UnwrapNullableType().GetNamespaces())
            {
                yield return ns;
            }
        }
        else
        {
            if (type.Namespace is not null)
            {
                yield return type.Namespace;
            }
        }

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
}


internal static class EntityFrameworkMemberInfoExtensions
{
    public static Type GetMemberType(this MemberInfo memberInfo)
        => (memberInfo as PropertyInfo)?.PropertyType ?? ((FieldInfo)memberInfo).FieldType;
}
