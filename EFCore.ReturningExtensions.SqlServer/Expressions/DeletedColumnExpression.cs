using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ReturningExtensions.SqlServer.Expressions;

internal class DeletedColumnExpression(
    string name,
    string tableAlias,
    Type type,
    RelationalTypeMapping? typeMapping,
    bool nullable)
    : ColumnExpression(name, tableAlias, type, typeMapping, nullable);