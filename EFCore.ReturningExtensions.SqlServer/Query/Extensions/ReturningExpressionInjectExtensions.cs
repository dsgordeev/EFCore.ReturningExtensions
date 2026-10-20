using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace EFCore.ReturningExtensions.SqlServer.Query.Extensions;

internal static class ReturningExpressionInjectExtensions
{
    private static readonly PropertyInfo IsMutableProperty = typeof(SelectExpression).GetProperty(
        "IsMutable",
        BindingFlags.NonPublic | BindingFlags.GetProperty | BindingFlags.Instance)!;

    private static readonly FieldInfo ProjectionMappingField = typeof(SelectExpression).GetField(
        "_projectionMapping",
        BindingFlags.NonPublic | BindingFlags.GetField | BindingFlags.Instance)!;

    private static readonly FieldInfo SqlAliasManagerField = typeof(SelectExpression).GetField(
        "_sqlAliasManager",
        BindingFlags.NonPublic | BindingFlags.GetField | BindingFlags.Instance)!;

    public static SelectExpression AppendExpressionAnnotation<T>(this SelectExpression origin, T expression)
    {
        var annotated = new SelectExpression(
            origin.Alias,
            tables: [.. origin.Tables],
            origin.Predicate,
            groupBy: [.. origin.GroupBy],
            origin.Having,
            projections: [.. origin.Projection],
            origin.IsDistinct,
            orderings: [.. origin.Orderings],
            origin.Offset,
            origin.Limit,
            tags: new ReadOnlySet<string>(origin.Tags),
            annotations: new ReadOnlyDictionary<string, IAnnotation>(
                new Dictionary<string, IAnnotation>
                {
                    [typeof(T).Name] = new Annotation(typeof(T).Name, expression)
                }),
            sqlAliasManager: null! /* gonna be updated below */,
            isMutable: false
        );

        IsMutableProperty.SetValue(annotated, IsMutableProperty.GetValue(origin));
        ProjectionMappingField.SetValue(annotated, ProjectionMappingField.GetValue(origin));
        SqlAliasManagerField.SetValue(annotated, SqlAliasManagerField.GetValue(origin));

        return annotated;
    }
}
