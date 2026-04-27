using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System.ComponentModel;
using System.Linq.Expressions;

namespace EFCore.ReturningExtensions.NonQueryPatch.Expressions;

public sealed class UpdateExpression : Expression, IPrintableExpression
{
    public UpdateExpression(TableExpression table, SelectExpression selectExpression, IReadOnlyList<ColumnValueSetter> columnValueSetters)
        : this(table, selectExpression, columnValueSetters, new HashSet<string>())
    {
    }

    private UpdateExpression(
        TableExpression table,
        SelectExpression selectExpression,
        IReadOnlyList<ColumnValueSetter> columnValueSetters,
        ISet<string> tags)
    {
        Table = table;
        SelectExpression = selectExpression;
        ColumnValueSetters = columnValueSetters;
        Tags = tags;
    }

    public ISet<string> Tags { get; }

    public TableExpression Table { get; }

    public SelectExpression SelectExpression { get; }

    public IReadOnlyList<ColumnValueSetter> ColumnValueSetters { get; }

    public UpdateExpression ApplyTags(ISet<string> tags)
        => new(Table, SelectExpression, ColumnValueSetters, tags);

    /// <inheritdoc />
    public override Type Type
        => typeof(object);

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var selectExpression = (SelectExpression)visitor.Visit(SelectExpression);
        List<ColumnValueSetter>? columnValueSetters = null;
        for (var (i, n) = (0, ColumnValueSetters.Count); i < n; i++)
        {
            var columnValueSetter = ColumnValueSetters[i];
            var newValue = (SqlExpression)visitor.Visit(columnValueSetter.Value);
            if (columnValueSetters != null)
            {
                columnValueSetters.Add(new ColumnValueSetter(columnValueSetter.Column, newValue));
            }
            else if (!ReferenceEquals(newValue, columnValueSetter.Value))
            {
                columnValueSetters = new List<ColumnValueSetter>(n);
                for (var j = 0; j < i; j++)
                {
                    columnValueSetters.Add(ColumnValueSetters[j]);
                }

                columnValueSetters.Add(new ColumnValueSetter(columnValueSetter.Column, newValue));
            }
        }

        return selectExpression != SelectExpression
            || columnValueSetters != null
                ? new UpdateExpression(Table, selectExpression, columnValueSetters ?? ColumnValueSetters)
                : this;
    }

    public UpdateExpression Update(SelectExpression selectExpression, IReadOnlyList<ColumnValueSetter> columnValueSetters)
        => selectExpression != SelectExpression || !ColumnValueSetters.SequenceEqual(columnValueSetters)
            ? new UpdateExpression(Table, selectExpression, columnValueSetters, Tags)
            : this;

    /// <inheritdoc />
    public void Print(ExpressionPrinter expressionPrinter)
    {
        foreach (var tag in Tags)
        {
            expressionPrinter.Append($"-- {tag}");
        }

        expressionPrinter.AppendLine();
        expressionPrinter.AppendLine($"UPDATE {Table.Name} AS {Table.Alias}");
        expressionPrinter.AppendLine("SET ");
        expressionPrinter.Visit(ColumnValueSetters[0].Column);
        expressionPrinter.Append(" = ");
        expressionPrinter.Visit(ColumnValueSetters[0].Value);
        using (expressionPrinter.Indent())
        {
            foreach (var columnValueSetter in ColumnValueSetters.Skip(1))
            {
                expressionPrinter.AppendLine(",");
                expressionPrinter.Visit(columnValueSetter.Column);
                expressionPrinter.Append(" = ");
                expressionPrinter.Visit(columnValueSetter.Value);
            }
        }

        expressionPrinter.AppendLine();
        expressionPrinter.Visit(SelectExpression);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj != null
            && (ReferenceEquals(this, obj)
                || obj is UpdateExpression updateExpression
                && Equals(updateExpression));

    private bool Equals(UpdateExpression updateExpression)
        => Table == updateExpression.Table
            && SelectExpression == updateExpression.SelectExpression
            && ColumnValueSetters.SequenceEqual(updateExpression.ColumnValueSetters);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Table);
        hash.Add(SelectExpression);
        foreach (var item in ColumnValueSetters)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
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