using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System.ComponentModel;
using System.Linq.Expressions;

namespace EFCore.ReturningExtensions.NonQueryPatch.Expressions;

public sealed class UpdateExpression : Expression, IPrintableExpression
{
    private readonly IEntityType _entityType;
    public bool Returning { get; private set; }

    /// <summary>
    ///     Creates a new instance of the <see cref="UpdateExpression" /> class.
    /// </summary>
    /// <param name="table">A table on which the update operation is being applied.</param>
    /// <param name="selectExpression">A select expression which is used to determine which rows to update and to get data from additional tables.</param>
    /// <param name="columnValueSetters">
    ///     A list of <see cref="ColumnValueSetter" /> which specifies columns and their corresponding values to
    ///     update.
    /// </param>
    /// <param name="entityType"></param>
    /// <param name="returning"></param>
    public UpdateExpression(TableExpression table, SelectExpression selectExpression,
        IReadOnlyList<ColumnValueSetter> columnValueSetters, IEntityType entityType, bool returning = false)
        : this(table, selectExpression, columnValueSetters, new HashSet<string>(), entityType, returning)
    {
    }

    private UpdateExpression(TableExpression table,
        SelectExpression selectExpression,
        IReadOnlyList<ColumnValueSetter> columnValueSetters,
        ISet<string> tags, IEntityType entityType, bool returning = false)
    {
        _entityType = entityType;
        Table = table;
        SelectExpression = selectExpression;
        ColumnValueSetters = columnValueSetters;
        Tags = tags;
        Returning = returning;
    }

    /// <summary>
    ///     The list of tags applied to this <see cref="UpdateExpression" />.
    /// </summary>
    public ISet<string> Tags { get; }

    /// <summary>
    ///     The table on which the update operation is being applied.
    /// </summary>
    public TableExpression Table { get; }

    /// <summary>
    ///     The select expression which is used to determine which rows to update and to get data from additional tables.
    /// </summary>
    public SelectExpression SelectExpression { get; }

    /// <summary>
    ///     The list of <see cref="ColumnValueSetter" /> which specifies columns and their corresponding values to update.
    /// </summary>
    public IReadOnlyList<ColumnValueSetter> ColumnValueSetters { get; }

    /// <summary>
    ///     Applies a given set of tags.
    /// </summary>
    /// <param name="tags">A list of tags to apply.</param>
    public UpdateExpression ApplyTags(ISet<string> tags)
        => new(Table, SelectExpression, ColumnValueSetters, tags, _entityType, returning: Returning);

    /// <inheritdoc />
    public override Type Type
        => Returning ? _entityType.ClrType : typeof(object);

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
            ? new UpdateExpression(Table, selectExpression, columnValueSetters ?? ColumnValueSetters, _entityType)
            : this;
    }

    /// <summary>
    ///     Creates a new expression that is like this one, but using the supplied children. If all of the children are the same, it will
    ///     return this expression.
    /// </summary>
    /// <param name="selectExpression">The <see cref="SelectExpression" /> property of the result.</param>
    /// <param name="columnValueSetters">The <see cref="ColumnValueSetters" /> property of the result.</param>
    /// <returns>This expression if no children changed, or an expression with the updated children.</returns>
    public UpdateExpression Update(SelectExpression selectExpression, IReadOnlyList<ColumnValueSetter> columnValueSetters)
        => selectExpression != SelectExpression || !ColumnValueSetters.SequenceEqual(columnValueSetters)
            ? new UpdateExpression(Table, selectExpression, columnValueSetters, Tags, _entityType)
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