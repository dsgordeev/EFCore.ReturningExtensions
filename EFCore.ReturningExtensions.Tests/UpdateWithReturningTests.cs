using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests
{
    public abstract class UpdateWithReturningTests<TDbContext> : IDisposable
        where TDbContext : UsersDbContext
    {
        private protected ServiceProvider ServiceProvider = new ServiceCollection()
            .AddDbContext<SqlUsersDbContext>()
            .AddDbContext<PgUsersDbContext>()
            .AddDbContext<Pg18UsersDbContext>()
            .AddDbContext<SqliteUsersDbContext>()
            .BuildServiceProvider();

        protected TDbContext GetDbContext() => ServiceProvider.CreateScope().ServiceProvider.GetRequiredService<TDbContext>();

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            ServiceProvider.Dispose();
        }
    }
}
