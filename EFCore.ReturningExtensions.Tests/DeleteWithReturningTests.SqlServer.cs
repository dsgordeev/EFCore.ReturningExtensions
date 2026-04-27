using EFCore.ReturningExtensions.Extensions;
using EFCore.ReturningExtensions.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ReturningExtensions.Tests;

public sealed partial class DeleteWithReturningTests
{
    [TestClass]
    public sealed class SqlServer : DeleteWithReturningTests<SqlUsersDbContext>
    {
        [TestMethod]
        public void Deleted_WhenParameter_ReturnDeleteWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            var hundred = 10 * 10;

            // Act
            var query = ctx.Users
                .AsNoTracking()
                .Where(p => p.Name == "A" || p.Name == "B")
                .Join(ctx.Users.Where(p => p.Name.Length < hundred * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Deleted(x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual($"""
                         DECLARE @p int = 10000;

                         DELETE [u]
                         OUTPUT [DELETED].[Id], [DELETED].[Name] + N'_' AS [Name]
                         FROM [User] AS [u]
                         INNER JOIN (
                             SELECT [u0].[Id], [u0].[Name]
                             FROM [User] AS [u0]
                             WHERE CAST(LEN([u0].[Name]) AS int) < @p
                         ) AS [u1] ON [u].[Id] = [u1].[Id]
                         WHERE [u].[Name] = N'A' OR [u].[Name] = N'B'
                         """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenParameterLess_ReturnDeleteWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            // Act
            var query = ctx.Users
                .AsNoTracking()
                .Where(p => p.Name == "A" || p.Name == "B")
                .Join(ctx.Users.Where(p => p.Name.Length < 100 * 100), x => x.Id, x => x.Id, (x, y) => x)
                .Deleted(x => new { x.Id, Name = x.Name + "_" });

            // Assert
            Assert.AreEqual($"""
                         DELETE [u]
                         OUTPUT [DELETED].[Id], [DELETED].[Name] + N'_' AS [Name]
                         FROM [User] AS [u]
                         INNER JOIN (
                             SELECT [u0].[Id], [u0].[Name]
                             FROM [User] AS [u0]
                             WHERE CAST(LEN([u0].[Name]) AS int) < 10000
                         ) AS [u1] ON [u].[Id] = [u1].[Id]
                         WHERE [u].[Name] = N'A' OR [u].[Name] = N'B'
                         """, query.ToQueryString());
        }

        [TestMethod]
        public void Deleted_WhenInclude_ReturnDeleteWithOutputClause()
        {
            // Arrange
            using var ctx = GetDbContext();

            // Act
            var query = ctx.Accounts
                .AsNoTracking()
                .Include(x => x.Type)
                .ThenInclude(x => x.SubType)
                .Where(p => p.Name.Length < 100 && p.Type.TypeName.Length < 1000 && p.Type.SubType.StName.Length < 10000)
                .Deleted(x => x);

            // Assert
            Assert.AreEqual($"""
                             DELETE [a]
                             OUTPUT [DELETED].[Id], [DELETED].[Name], [DELETED].[TypeId], [a0].[Id], [a0].[SubTypeId], [a0].[TypeName], [a1].[Id], [a1].[StName]
                             FROM [Account] AS [a]
                             INNER JOIN [AccountType] AS [a0] ON [a].[TypeId] = [a0].[Id]
                             INNER JOIN [AccountSubType] AS [a1] ON [a0].[SubTypeId] = [a1].[Id]
                             WHERE CAST(LEN([a].[Name]) AS int) < 100 AND CAST(LEN([a0].[TypeName]) AS int) < 1000 AND CAST(LEN([a1].[StName]) AS int) < 10000
                             """, query.ToQueryString());
        }
    }
}