using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests
{
    public sealed partial class DeleteWithReturningTests
    {
        [TestClass]
        public sealed class SqlServer : DeleteWithReturningTests<SqlUsersDbContext>
        {
            [TestMethod]
            public void Deleted_WhenParameter_ReturnDeleteWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                var hundred = 10 * 10;

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name == "A" || p.Name == "B")
                    .Join(ctx.Users.Where(p => p.Name.Length < hundred * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Deleted(x => new { x.Id, Name = x.Name + "_" });

                // Assert
                Assert.AreEqual("""
                                DECLARE @__p_0 int = 10000;

                                DELETE [u]
                                OUTPUT [DELETED].[Id], [DELETED].[Name] + N'_' AS [Name]
                                FROM [User] AS [u]
                                INNER JOIN (
                                    SELECT [u0].[Id], [u0].[Name]
                                    FROM [User] AS [u0]
                                    WHERE CAST(LEN([u0].[Name]) AS int) < @__p_0
                                ) AS [t] ON [u].[Id] = [t].[Id]
                                WHERE [u].[Name] = N'A' OR [u].[Name] = N'B'
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Deleted_WhenParameterLess_ReturnDeleteWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name == "A" || p.Name == "B")
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Deleted(x => new { x.Id, Name = x.Name + "_" });

                // Assert
                Assert.AreEqual("""
                                DELETE [u]
                                OUTPUT [DELETED].[Id], [DELETED].[Name] + N'_' AS [Name]
                                FROM [User] AS [u]
                                INNER JOIN (
                                    SELECT [u0].[Id], [u0].[Name]
                                    FROM [User] AS [u0]
                                    WHERE CAST(LEN([u0].[Name]) AS int) < 10000
                                ) AS [t] ON [u].[Id] = [t].[Id]
                                WHERE [u].[Name] = N'A' OR [u].[Name] = N'B'
                                """, query.ToQueryString());
            }
        }
    }
}
