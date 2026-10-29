using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests;

public abstract class UpdateWithReturningTests<TDbContext> : IDisposable
    where TDbContext : UsersDbContext
{
    private ServiceProvider? _serviceProvider;

    [TestInitialize]
    public void TestInitialize()
    {
        _serviceProvider = new ServiceCollection()
            .AddDbContext<TDbContext>()
            .BuildServiceProvider();
    }

    protected TDbContext GetDbContext()
    {
        return _serviceProvider!.CreateScope().ServiceProvider.GetRequiredService<TDbContext>();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _serviceProvider?.Dispose();
    }
}