using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.SqlServer.Query.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Storage.Internal;
using System.Linq.Expressions;

namespace EFCore.ReturningExtensions.NonQueryPatch.Infrastructure;

public class SqlServerQueryCompilationContextFactoryPatch : SqlServerQueryCompilationContextFactory
{
    private readonly ISqlServerConnection _sqlServerConnection;

    public SqlServerQueryCompilationContextFactoryPatch(QueryCompilationContextDependencies dependencies,
        RelationalQueryCompilationContextDependencies relationalDependencies,
        ISqlServerConnection sqlServerConnection)
        : base(dependencies, relationalDependencies, sqlServerConnection)
    {
        _sqlServerConnection = sqlServerConnection;
    }

    public override QueryCompilationContext Create(bool async)
    {
        return new SqlServerQueryCompilationContextPatch(
            Dependencies, RelationalDependencies, async, _sqlServerConnection.IsMultipleActiveResultSetsEnabled);
    }
}

public class SqlServerQueryCompilationContextPatch : SqlServerQueryCompilationContext
{
    public SqlServerQueryCompilationContextPatch(QueryCompilationContextDependencies dependencies,
        RelationalQueryCompilationContextDependencies relationalDependencies, bool async,
        bool multipleActiveResultSetsEnabled)
        : base(dependencies, relationalDependencies, async, multipleActiveResultSetsEnabled)
    {
    }

    public override Func<QueryContext, TResult> CreateQueryExecutor<TResult>(Expression query)
    {
        return base.CreateQueryExecutor<TResult>(query);
    }
}