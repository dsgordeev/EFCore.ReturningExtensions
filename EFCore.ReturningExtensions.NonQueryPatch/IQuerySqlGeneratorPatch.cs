using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ReturningExtensions.NonQueryPatch;

public interface IQuerySqlGeneratorPatch
{
    IRelationalCommand GetCommand2(Expression queryExpression);
}