using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ReturningExtensions.Tests
{
    public sealed partial class UpdateWithReturningTests
    {
        [TestClass]
        public sealed class SqlServer : UpdateWithReturningTests<SqlUsersDbContext>
        {
            [TestMethod]
            public void Updated_WhenAndMixed_ReturnUpdateWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name.Length < 100)
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        (x, y) => new { y.Id, Name2 = x.Name + "<|" + y.Name });

                // Assert
                Assert.AreEqual("""
                                UPDATE [u]
                                SET [u].[Name] = SUBSTRING([u].[Name] + N'_', 0 + 1, 1)
                                OUTPUT [DELETED].[Id], [INSERTED].[Name] + N'<|' + [DELETED].[Name] AS [Name2]
                                FROM [User] AS [u]
                                INNER JOIN (
                                    SELECT [u0].[Id], [u0].[Name]
                                    FROM [User] AS [u0]
                                    WHERE CAST(LEN([u0].[Name]) AS int) < 10000
                                ) AS [u1] ON [u].[Id] = [u1].[Id]
                                WHERE CAST(LEN([u].[Name]) AS int) < 100
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Updated_WhenAndDeletedOnly_ReturnUpdateWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name.Length < 100)
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        (x, y) => y);

                // Assert
                Assert.AreEqual("""
                                UPDATE [u]
                                SET [u].[Name] = SUBSTRING([u].[Name] + N'_', 0 + 1, 1)
                                OUTPUT [DELETED].[Id], [DELETED].[Name]
                                FROM [User] AS [u]
                                INNER JOIN (
                                    SELECT [u0].[Id], [u0].[Name]
                                    FROM [User] AS [u0]
                                    WHERE CAST(LEN([u0].[Name]) AS int) < 10000
                                ) AS [u1] ON [u].[Id] = [u1].[Id]
                                WHERE CAST(LEN([u].[Name]) AS int) < 100
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Updated_WhenFallback_ReturnUpdateWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name.Length < 100)
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        (x, y) => x);

                // Assert
                Assert.AreEqual("""
                                UPDATE [u]
                                SET [u].[Name] = SUBSTRING([u].[Name] + N'_', 0 + 1, 1)
                                OUTPUT [INSERTED].[Id], [INSERTED].[Name]
                                FROM [User] AS [u]
                                INNER JOIN (
                                    SELECT [u0].[Id], [u0].[Name]
                                    FROM [User] AS [u0]
                                    WHERE CAST(LEN([u0].[Name]) AS int) < 10000
                                ) AS [u1] ON [u].[Id] = [u1].[Id]
                                WHERE CAST(LEN([u].[Name]) AS int) < 100
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Updated_WhenAndParameter_ReturnUpdateWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                var hundred = 10 * 10;

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name.Length < hundred)
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * hundred), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        x => x);

                // Assert
                Assert.AreEqual("""
                                DECLARE @p int = 10000;
                                DECLARE @hundred int = 100;

                                UPDATE [u]
                                SET [u].[Name] = SUBSTRING([u].[Name] + N'_', 0 + 1, 1)
                                OUTPUT [INSERTED].[Id], [INSERTED].[Name]
                                FROM [User] AS [u]
                                INNER JOIN (
                                    SELECT [u0].[Id], [u0].[Name]
                                    FROM [User] AS [u0]
                                    WHERE CAST(LEN([u0].[Name]) AS int) < @p
                                ) AS [u1] ON [u].[Id] = [u1].[Id]
                                WHERE CAST(LEN([u].[Name]) AS int) < @hundred
                                """, query.ToQueryString());
            }

            [TestMethod]
            public void Updated_When_ReturnUpdateWithOutputClause()
            {
                // Arrange
                using var scope = ServiceProvider.CreateScope();
                using var ctx = GetDbContext();

                // Act
                var query = ctx.Users
                    .AsNoTracking()
                    .Where(p => p.Name.Length < 100)
                    .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                    .Updated(s =>
                            s.SetProperty(p => p.Name, p => (p.Name + "_").Substring(0, 1)),
                        x => new { x.Id, Name = x.Name + "_" });

                // Assert
                Assert.AreEqual("""
                                UPDATE [u]
                                SET [u].[Name] = SUBSTRING([u].[Name] + N'_', 0 + 1, 1)
                                OUTPUT [INSERTED].[Id], [INSERTED].[Name] + N'_' AS [Name]
                                FROM [User] AS [u]
                                INNER JOIN (
                                    SELECT [u0].[Id], [u0].[Name]
                                    FROM [User] AS [u0]
                                    WHERE CAST(LEN([u0].[Name]) AS int) < 10000
                                ) AS [u1] ON [u].[Id] = [u1].[Id]
                                WHERE CAST(LEN([u].[Name]) AS int) < 100
                                """, query.ToQueryString());
            }
        }
    }
}
